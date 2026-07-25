using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AiProxy.Auth;
using AiProxy.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiProxy.Proxy;

/// <summary>
/// Ollama-compatible endpoints, backed by GitHub Copilot's OpenAI-shaped chat API.
///
/// VS Code's built-in Ollama provider lets the user enter a base URL, so this is the only
/// VS Code provider we can target without shipping an extension. We emulate just enough of
/// the Ollama wire protocol for VS Code to discover models and run streaming chat.
/// </summary>
public static class OllamaEndpoints
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // ----------------------------------------------------------------------
    // GET /api/version
    // ----------------------------------------------------------------------
    public static IResult Version()
    {
        // VS Code's Ollama provider parses this and gates on a minimum version,
        // so we must report a real-looking, recent Ollama version.
        return Results.Json(new { version = "0.6.4" });
    }

    // ----------------------------------------------------------------------
    // GET /api/tags  -> { "models": [ { name, model, modified_at, size, digest, details } ] }
    // ----------------------------------------------------------------------
    public static async Task<IResult> Tags(IEnumerable<IAuthProvider> providers, CancellationToken cancellationToken)
    {
        var byProvider = await ProviderResolver.ListAllAsync(providers, cancellationToken).ConfigureAwait(false);
        var modifiedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK");

        var models = new List<object>();
        foreach (var (provider, ids) in byProvider)
        {
            var infos = await provider.GetModelInfosAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in ids)
            {
                infos.TryGetValue(id, out var info);
                var family = info?.Family is { Length: > 0 } f ? f.Replace('.', '_') : provider.Name;
                models.Add(new
                {
                    name = id,
                    model = id,
                    modified_at = modifiedAt,
                    size = 0L,
                    digest = "",
                    details = new
                    {
                        parent_model = "",
                        format = "gguf",
                        family,
                        families = new[] { family },
                        parameter_size = "",
                        quantization_level = ""
                    }
                });
            }
        }

        return Results.Json(new { models });
    }

    // ----------------------------------------------------------------------
    // POST /api/show  body: { "model" | "name" : "..." }
    // ----------------------------------------------------------------------
    public static async Task<IResult> Show(HttpContext context, IEnumerable<IAuthProvider> providers, CancellationToken cancellationToken)
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: cancellationToken).ConfigureAwait(false);
        string? requested = null;
        if (doc.RootElement.TryGetProperty("model", out var m1) && m1.ValueKind == JsonValueKind.String)
        {
            requested = m1.GetString();
        }
        else if (doc.RootElement.TryGetProperty("name", out var m2) && m2.ValueKind == JsonValueKind.String)
        {
            requested = m2.GetString();
        }

        if (string.IsNullOrEmpty(requested))
        {
            return Results.Json(new { error = "model not found" }, statusCode: 404);
        }

        var provider = await ProviderResolver.ResolveForModelAsync(providers, requested, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return Results.Json(new { error = "model not found" }, statusCode: 404);
        }

        var infos = await provider.GetModelInfosAsync(cancellationToken).ConfigureAwait(false);
        infos.TryGetValue(requested, out var info);
        var arch = provider.Name;
        var family = info?.Family is { Length: > 0 } f ? f.Replace('.', '_') : provider.Name;
        var contextLength = info?.MaxContextWindowTokens ?? 0;

        var modelInfo = new Dictionary<string, object?>
        {
            ["general.architecture"] = arch,
            ["general.basename"] = requested,
            ["general.name"] = info?.Name ?? requested
        };
        if (contextLength > 0)
        {
            modelInfo[$"{arch}.context_length"] = contextLength;
            // Belt-and-braces: a few clients read this generic key.
            modelInfo["general.context_length"] = contextLength;
        }

        // Expose context window via the Modelfile-style parameters field too — some
        // Ollama clients read num_ctx from there to size the token gauge.
        var parametersText = contextLength > 0 ? $"num_ctx {contextLength}\n" : "";

        // VS Code reads exactly two capability strings from here: "tools" (tool calling)
        // and "vision" (image attachments). Advertising one the model lacks turns a clean
        // client-side block into an upstream 400 mid-request.
        var capabilities = new List<string> { "completion" };
        if (info?.SupportsToolCalls != false)
        {
            capabilities.Add("tools");
        }
        if (info?.SupportsVision == true)
        {
            capabilities.Add("vision");
        }

        return Results.Json(new
        {
            license = "",
            modelfile = $"# Proxied via AiProxy\nFROM {requested}\n",
            parameters = parametersText,
            template = "",
            details = new
            {
                parent_model = "",
                format = "gguf",
                family,
                families = new[] { family },
                parameter_size = "",
                quantization_level = ""
            },
            model_info = modelInfo,
            capabilities
        });
    }

    // ----------------------------------------------------------------------
    // POST /api/chat
    // Request: { model, messages, stream?, options? }
    // Response (stream=true, default): NDJSON of { model, created_at, message:{role,content}, done }
    // Response (stream=false): single JSON object with full message and done=true
    // ----------------------------------------------------------------------
    public static async Task Chat(
        HttpContext context,
        IEnumerable<IAuthProvider> providers,
        ChatPipeline pipeline,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AiProxy.Ollama.Chat");
        var cancellationToken = context.RequestAborted;

        // Parse Ollama request.
        OllamaChatRequest req;
        try
        {
            using var ms = new MemoryStream();
            await context.Request.Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);
            req = JsonSerializer.Deserialize<OllamaChatRequest>(ms.ToArray(), JsonOptions)
                  ?? throw new JsonException("Empty body.");
        }
        catch (JsonException ex)
        {
            await WriteJsonErrorAsync(context, 400, $"Invalid JSON: {ex.Message}");
            return;
        }

        if (string.IsNullOrEmpty(req.Model))
        {
            await WriteJsonErrorAsync(context, 400, "Missing 'model'.");
            return;
        }

        var provider = await ProviderResolver.ResolveForModelAsync(providers, req.Model, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            await WriteJsonErrorAsync(context, 404, $"model '{req.Model}' not found");
            return;
        }

        // Default for Ollama is stream=true.
        var isStream = req.Stream ?? true;

        // Translate the Ollama request into the internal (OpenAI-shaped) pipeline request.
        var messages = new JsonArray();
        foreach (var m in req.Messages ?? new List<OllamaMessage>())
        {
            messages.Add(ConvertMessage(m));
        }

        var upstreamRequest = new JsonObject
        {
            ["model"] = req.Model,
            ["messages"] = messages,
            ["stream"] = isStream
        };
        if (req.Options is { } opts)
        {
            if (opts.Temperature is { } t) upstreamRequest["temperature"] = t;
            if (opts.TopP is { } tp) upstreamRequest["top_p"] = tp;
            if (opts.NumPredict is { } np) upstreamRequest["max_tokens"] = np;
            if (opts.Stop is { Length: > 0 } stop)
            {
                var stopArray = new JsonArray();
                foreach (var s in stop) stopArray.Add(s);
                upstreamRequest["stop"] = stopArray;
            }
        }
        if (req.Tools is { Count: > 0 } tools)
        {
            var toolArray = new JsonArray();
            foreach (var tool in tools) toolArray.Add(JsonNode.Parse(tool.GetRawText()));
            upstreamRequest["tools"] = toolArray;
        }

        var pipelineContext = new ChatPipelineContext
        {
            Http = context,
            Surface = ClientSurface.Ollama,
            Model = req.Model,
            IsStreaming = isStream,
            UpstreamRequest = upstreamRequest,
            Provider = provider,
            Logger = logger
        };

        try
        {
            await pipeline.InvokeAsync(pipelineContext).ConfigureAwait(false);
        }
        catch (UpstreamException ex)
        {
            logger.LogWarning("Upstream returned {Status}: {Body}", ex.StatusCode, ex.Body);
            await WriteJsonErrorAsync(context, ex.StatusCode, ex.Body);
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream request failed.");
            await WriteJsonErrorAsync(context, 502, $"Upstream error: {ex.Message}");
            return;
        }

        if (isStream)
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/x-ndjson";
            await WriteNdjsonStreamAsync(req.Model, pipelineContext.ResponseChunks, context.Response.Body, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            var ollama = await AggregateNonStreamingAsync(req.Model, pipelineContext.ResponseChunks, cancellationToken).ConfigureAwait(false);
            await JsonSerializer.SerializeAsync(context.Response.Body, ollama, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
    }

    // ----------------------------------------------------------------------
    // Translation helpers
    // ----------------------------------------------------------------------

    internal static JsonObject ConvertMessage(OllamaMessage m)
    {
        var obj = new JsonObject
        {
            ["role"] = m.Role
        };

        // Ollama carries attachments in a separate base64 'images' array; the OpenAI wire
        // format expects them as image_url content parts alongside the text.
        if (m.Images is { Count: > 0 } images)
        {
            var parts = new JsonArray();
            if (!string.IsNullOrEmpty(m.Content))
            {
                parts.Add(new JsonObject { ["type"] = "text", ["text"] = m.Content });
            }
            foreach (var image in images)
            {
                if (string.IsNullOrWhiteSpace(image)) continue;
                parts.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = ToDataUrl(image) }
                });
            }
            obj["content"] = parts;
        }
        else
        {
            obj["content"] = m.Content ?? "";
        }

        if (m.ToolCalls is { Count: > 0 } tc)
        {
            var toolArray = new JsonArray();
            foreach (var call in tc) toolArray.Add(ConvertToolCall(call));
            obj["tool_calls"] = toolArray;
        }

        // A tool result is only accepted upstream when it points back at the call it answers.
        if (!string.IsNullOrEmpty(m.ToolCallId)) obj["tool_call_id"] = m.ToolCallId;

        var name = !string.IsNullOrEmpty(m.Name) ? m.Name : m.ToolName;
        if (!string.IsNullOrEmpty(name)) obj["name"] = name;
        return obj;
    }

    /// <summary>
    /// Rewrites an Ollama tool call into the OpenAI shape: the upstream requires an <c>id</c>
    /// and a <c>type</c>, and expects <c>function.arguments</c> as a JSON string where Ollama
    /// clients send it as an object.
    /// </summary>
    private static JsonObject ConvertToolCall(JsonElement call)
    {
        string? id = null;
        if (call.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
        {
            id = idElement.GetString();
        }

        string? name = null;
        var arguments = "{}";
        if (call.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
        {
            if (function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
            {
                name = nameElement.GetString();
            }
            if (function.TryGetProperty("arguments", out var argumentsElement))
            {
                arguments = argumentsElement.ValueKind == JsonValueKind.String
                    ? argumentsElement.GetString() ?? "{}"
                    : argumentsElement.GetRawText();
            }
        }

        return new JsonObject
        {
            ["id"] = string.IsNullOrEmpty(id) ? $"call_{Guid.NewGuid():N}" : id,
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name ?? "",
                ["arguments"] = arguments
            }
        };
    }

    /// <summary>
    /// Ollama sends bare base64 payloads with no media type, so sniff the magic bytes to
    /// build the data URL the OpenAI image_url part requires.
    /// </summary>
    private static string ToDataUrl(string image)
    {
        var value = image.Trim();
        if (value.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return value;
        }

        var mediaType = "image/png";
        Span<byte> header = stackalloc byte[12];
        // Base64 decodes 4 chars -> 3 bytes; 16 chars is enough for the longest signature.
        var prefix = value.Length > 16 ? value[..16] : value;
        if (Convert.TryFromBase64String(PadBase64(prefix), header, out var written))
        {
            var bytes = header[..written];
            if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            {
                mediaType = "image/jpeg";
            }
            else if (bytes.Length >= 3 && bytes[0] == (byte)'G' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F')
            {
                mediaType = "image/gif";
            }
            else if (bytes.Length >= 12
                     && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
                     && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
            {
                mediaType = "image/webp";
            }
        }

        return $"data:{mediaType};base64,{value}";
    }

    private static string PadBase64(string value)
    {
        var usable = value.Length - (value.Length % 4);
        return value[..usable];
    }

    private static async Task WriteNdjsonStreamAsync(
        string model,
        IAsyncEnumerable<ChatResponseChunk> chunks,
        Stream outputStream,
        CancellationToken cancellationToken)
    {
        var lastFinishReason = "stop";
        var promptTokens = 0;
        var completionTokens = 0;
        var toolCalls = new ToolCallAccumulator();

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.PromptTokens is { } pt) promptTokens = pt;
            if (chunk.CompletionTokens is { } ct) completionTokens = ct;
            if (chunk.FinishReason is { } fr) lastFinishReason = fr;

            // Upstream spreads a single tool call over many frames (id and name first, then
            // argument text in pieces). Ollama clients report every call they see straight
            // through to the tool, so hold fragments back until the stream completes.
            if (chunk.ToolCalls is { Count: > 0 } fragments)
            {
                foreach (var fragment in fragments) toolCalls.Add(fragment);
            }

            // Skip frames that have nothing to deliver.
            if (string.IsNullOrEmpty(chunk.ContentDelta))
            {
                continue;
            }

            var frame = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["created_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK"),
                ["message"] = BuildMessage(chunk.ContentDelta, null),
                ["done"] = false
            };
            await WriteNdjsonAsync(outputStream, frame, cancellationToken).ConfigureAwait(false);
        }

        if (toolCalls.Build() is { Count: > 0 } completedCalls)
        {
            var frame = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["created_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK"),
                ["message"] = BuildMessage("", completedCalls),
                ["done"] = false
            };
            await WriteNdjsonAsync(outputStream, frame, cancellationToken).ConfigureAwait(false);
        }

        // Final "done" frame.
        var done = new Dictionary<string, object?>
        {
            ["model"] = model,
            ["created_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK"),
            ["message"] = new { role = "assistant", content = "" },
            ["done_reason"] = lastFinishReason,
            ["done"] = true,
            ["total_duration"] = 0,
            ["load_duration"] = 0,
            ["prompt_eval_count"] = promptTokens,
            ["prompt_eval_duration"] = 0,
            ["eval_count"] = completionTokens,
            ["eval_duration"] = 0
        };
        await WriteNdjsonAsync(outputStream, done, cancellationToken).ConfigureAwait(false);
    }

    private static object BuildMessage(string content, IReadOnlyList<object>? toolCalls)
    {
        var dict = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["content"] = content
        };
        if (toolCalls is { Count: > 0 })
        {
            dict["tool_calls"] = toolCalls;
        }
        return dict;
    }

    private static async Task WriteNdjsonAsync(Stream stream, object payload, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(new byte[] { (byte)'\n' }, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object> AggregateNonStreamingAsync(
        string model,
        IAsyncEnumerable<ChatResponseChunk> chunks,
        CancellationToken cancellationToken)
    {
        var content = new System.Text.StringBuilder();
        var finishReason = "stop";
        var promptTokens = 0;
        var completionTokens = 0;
        var toolCalls = new ToolCallAccumulator();

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.ContentDelta is { } c) content.Append(c);
            if (chunk.FinishReason is { } fr) finishReason = fr;
            if (chunk.PromptTokens is { } pt) promptTokens = pt;
            if (chunk.CompletionTokens is { } ct) completionTokens = ct;
            if (chunk.ToolCalls is { Count: > 0 } fragments)
            {
                foreach (var fragment in fragments) toolCalls.Add(fragment);
            }
        }

        return new
        {
            model,
            created_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK"),
            message = BuildMessage(content.ToString(), toolCalls.Build()),
            done_reason = finishReason,
            done = true,
            total_duration = 0,
            load_duration = 0,
            prompt_eval_count = promptTokens,
            prompt_eval_duration = 0,
            eval_count = completionTokens,
            eval_duration = 0
        };
    }

    private static async Task WriteJsonErrorAsync(HttpContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new { error = message }, JsonOptions);
        await context.Response.WriteAsync(json);
    }

    /// <summary>
    /// Reassembles OpenAI-style streamed tool-call fragments into whole Ollama tool calls.
    /// Also accepts an already-complete call as a single fragment, which is what the
    /// non-streaming path produces.
    /// </summary>
    internal sealed class ToolCallAccumulator
    {
        private readonly Dictionary<int, Entry> _entries = new();
        private int _fallbackIndex;

        public void Add(JsonElement fragment)
        {
            if (fragment.ValueKind != JsonValueKind.Object) return;

            var index = fragment.TryGetProperty("index", out var indexElement) && indexElement.TryGetInt32(out var i)
                ? i
                : _fallbackIndex++;

            if (!_entries.TryGetValue(index, out var entry))
            {
                entry = new Entry();
                _entries[index] = entry;
            }

            if (fragment.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                entry.Id = idElement.GetString();
            }
            if (fragment.TryGetProperty("function", out var function) && function.ValueKind == JsonValueKind.Object)
            {
                if (function.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String)
                {
                    entry.Name = nameElement.GetString();
                }
                if (function.TryGetProperty("arguments", out var argumentsElement) && argumentsElement.ValueKind == JsonValueKind.String)
                {
                    entry.Arguments.Append(argumentsElement.GetString());
                }
            }
        }

        public List<object> Build()
        {
            var result = new List<object>();
            foreach (var index in _entries.Keys.Order())
            {
                var entry = _entries[index];
                if (string.IsNullOrEmpty(entry.Name)) continue;

                result.Add(new Dictionary<string, object?>
                {
                    ["id"] = string.IsNullOrEmpty(entry.Id) ? $"call_{index}" : entry.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = entry.Name,
                        // VS Code passes function.arguments straight through as the tool's
                        // input object, so it must be parsed JSON rather than the raw string
                        // the OpenAI wire format uses.
                        ["arguments"] = ParseArguments(entry.Arguments.ToString())
                    }
                });
            }
            return result;
        }

        private static JsonNode ParseArguments(string arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments)) return new JsonObject();
            try
            {
                return JsonNode.Parse(arguments) ?? new JsonObject();
            }
            catch (JsonException)
            {
                return new JsonObject();
            }
        }

        private sealed class Entry
        {
            public string? Id { get; set; }
            public string? Name { get; set; }
            public System.Text.StringBuilder Arguments { get; } = new();
        }
    }

    // ----------------------------------------------------------------------
    // Request DTOs
    //
    // Ollama uses snake_case on the wire. The Web serializer defaults to camelCase and its
    // case-insensitive matching does not bridge underscores, so every snake_case field needs
    // an explicit name or it binds to null without error.
    // ----------------------------------------------------------------------

    public sealed class OllamaChatRequest
    {
        public string? Model { get; set; }
        public List<OllamaMessage>? Messages { get; set; }
        public bool? Stream { get; set; }
        public OllamaOptions? Options { get; set; }
        public List<JsonElement>? Tools { get; set; }
    }

    public sealed class OllamaMessage
    {
        public string? Role { get; set; }
        public string? Content { get; set; }
        public string? Name { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<JsonElement>? ToolCalls { get; set; }

        [JsonPropertyName("tool_call_id")]
        public string? ToolCallId { get; set; }

        [JsonPropertyName("tool_name")]
        public string? ToolName { get; set; }

        public List<string>? Images { get; set; }
    }

    public sealed class OllamaOptions
    {
        public double? Temperature { get; set; }

        [JsonPropertyName("top_p")]
        public double? TopP { get; set; }

        [JsonPropertyName("num_predict")]
        public int? NumPredict { get; set; }

        public string[]? Stop { get; set; }
    }
}
