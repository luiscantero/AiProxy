using System.Text.Json;
using System.Text.Json.Nodes;
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
            capabilities = new[] { "completion", "tools" }
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

    private static JsonObject ConvertMessage(OllamaMessage m)
    {
        // Pass content through; ignore images for now (Copilot won't accept them anyway).
        var obj = new JsonObject
        {
            ["role"] = m.Role,
            ["content"] = m.Content ?? ""
        };
        if (m.ToolCalls is { Count: > 0 } tc)
        {
            var toolArray = new JsonArray();
            foreach (var call in tc) toolArray.Add(JsonNode.Parse(call.GetRawText()));
            obj["tool_calls"] = toolArray;
        }
        if (!string.IsNullOrEmpty(m.Name)) obj["name"] = m.Name;
        return obj;
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

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.PromptTokens is { } pt) promptTokens = pt;
            if (chunk.CompletionTokens is { } ct) completionTokens = ct;
            if (chunk.FinishReason is { } fr) lastFinishReason = fr;

            // Skip frames that have nothing to deliver.
            if (chunk.ContentDelta is null && chunk.ToolCalls is null)
            {
                continue;
            }

            var frame = new Dictionary<string, object?>
            {
                ["model"] = model,
                ["created_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK"),
                ["message"] = BuildMessage(chunk.ContentDelta ?? "", chunk.ToolCalls),
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

    private static object BuildMessage(string content, IReadOnlyList<JsonElement>? toolCalls)
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

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.ContentDelta is { } c) content.Append(c);
            if (chunk.FinishReason is { } fr) finishReason = fr;
            if (chunk.PromptTokens is { } pt) promptTokens = pt;
            if (chunk.CompletionTokens is { } ct) completionTokens = ct;
        }

        return new
        {
            model,
            created_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK"),
            message = new { role = "assistant", content = content.ToString() },
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

    // ----------------------------------------------------------------------
    // Request DTOs
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
        public List<JsonElement>? ToolCalls { get; set; }
        public List<string>? Images { get; set; }
    }

    public sealed class OllamaOptions
    {
        public double? Temperature { get; set; }
        public double? TopP { get; set; }
        public int? NumPredict { get; set; }
        public string[]? Stop { get; set; }
    }
}
