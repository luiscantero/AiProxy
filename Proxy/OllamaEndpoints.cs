using System.Net.Http.Headers;
using System.Text.Json;
using AiProxy.Auth;
using AiProxy.Auth.Copilot;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
        var copilot = providers.FirstOrDefault(p => p.Name == CopilotAuthProvider.ProviderName);
        var selected = copilot is null
            ? Array.Empty<string>()
            : await copilot.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);

        var infos = copilot is null
            ? new Dictionary<string, AiProxy.Storage.ModelInfo>()
            : await copilot.GetModelInfosAsync(cancellationToken).ConfigureAwait(false);

        var modifiedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK");

        var models = selected.Select(id =>
        {
            infos.TryGetValue(id, out var info);
            var family = info?.Family is { Length: > 0 } f ? f.Replace('.', '_') : "copilot";
            return new
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
            };
        });

        return Results.Json(new { models });
    }

    // ----------------------------------------------------------------------
    // POST /api/show  body: { "model" | "name" : "..." }
    // ----------------------------------------------------------------------
    public static async Task<IResult> Show(HttpContext context, IEnumerable<IAuthProvider> providers, CancellationToken cancellationToken)
    {
        var copilot = providers.FirstOrDefault(p => p.Name == CopilotAuthProvider.ProviderName);
        if (copilot is null)
        {
            return Results.Json(new { error = "Copilot provider not registered." }, statusCode: 500);
        }

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

        var selected = await copilot.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(requested) || !selected.Contains(requested))
        {
            return Results.Json(new { error = "model not found" }, statusCode: 404);
        }

        var infos = await copilot.GetModelInfosAsync(cancellationToken).ConfigureAwait(false);
        infos.TryGetValue(requested, out var info);
        var arch = "copilot";
        var family = info?.Family is { Length: > 0 } f ? f.Replace('.', '_') : "copilot";
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
        IHttpClientFactory httpClientFactory,
        IOptions<AiProxyOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AiProxy.Ollama.Chat");
        var cancellationToken = context.RequestAborted;

        var copilot = providers.FirstOrDefault(p => p.Name == CopilotAuthProvider.ProviderName);
        if (copilot is null)
        {
            await WriteJsonErrorAsync(context, 500, "Copilot provider not registered.");
            return;
        }

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

        var allowed = await copilot.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);
        if (allowed.Count == 0)
        {
            await WriteJsonErrorAsync(context, 503, "No models available. Run 'AiProxy connect copilot' first.");
            return;
        }
        if (!allowed.Contains(req.Model))
        {
            await WriteJsonErrorAsync(context, 404, $"model '{req.Model}' not found");
            return;
        }

        // Default for Ollama is stream=true.
        var isStream = req.Stream ?? true;

        // Build OpenAI-shaped upstream request.
        var openAiRequest = new Dictionary<string, object?>
        {
            ["model"] = req.Model,
            ["messages"] = (req.Messages ?? new List<OllamaMessage>()).Select(ConvertMessage).ToList(),
            ["stream"] = isStream
        };
        if (isStream)
        {
            // OpenAI-style streaming omits the `usage` block by default; opting in
            // is required so we can fill the Ollama final frame's prompt_eval_count
            // and eval_count, which VS Code uses to render the context-window gauge.
            openAiRequest["stream_options"] = new { include_usage = true };
        }
        if (req.Options is { } opts)
        {
            if (opts.Temperature is { } t) openAiRequest["temperature"] = t;
            if (opts.TopP is { } tp) openAiRequest["top_p"] = tp;
            if (opts.NumPredict is { } np) openAiRequest["max_tokens"] = np;
            if (opts.Stop is { Length: > 0 } stop) openAiRequest["stop"] = stop;
        }
        if (req.Tools is { Count: > 0 } tools)
        {
            openAiRequest["tools"] = tools;
        }

        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(openAiRequest, JsonOptions);

        string upstreamBearer;
        try
        {
            upstreamBearer = await copilot.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to acquire upstream access token.");
            await WriteJsonErrorAsync(context, 503, ex.Message);
            return;
        }

        var upstreamBaseUrl = await copilot.GetUpstreamApiBaseUrlAsync(cancellationToken).ConfigureAwait(false)
                              ?? options.Value.Copilot.UpstreamBaseUrl;
        var upstreamUrl = upstreamBaseUrl.TrimEnd('/') + "/chat/completions";

        var http = httpClientFactory.CreateClient("upstream");
        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        upstreamRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstreamBearer);
        upstreamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(isStream ? "text/event-stream" : "application/json"));
        CopilotHeaders.Apply(upstreamRequest);
        upstreamRequest.Headers.TryAddWithoutValidation("OpenAI-Intent", "conversation-panel");

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await http.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream request failed.");
            await WriteJsonErrorAsync(context, 502, $"Upstream error: {ex.Message}");
            return;
        }

        try
        {
            if (!upstreamResponse.IsSuccessStatusCode)
            {
                var errorBody = await upstreamResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                logger.LogWarning("Upstream returned {Status}: {Body}", (int)upstreamResponse.StatusCode, errorBody);
                await WriteJsonErrorAsync(context, (int)upstreamResponse.StatusCode, errorBody);
                return;
            }

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            if (isStream)
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/x-ndjson";
                await TranslateSseToNdjsonAsync(req.Model, upstreamStream, context.Response.Body, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                context.Response.StatusCode = 200;
                context.Response.ContentType = "application/json";
                using var doc = await JsonDocument.ParseAsync(upstreamStream, cancellationToken: cancellationToken).ConfigureAwait(false);
                var ollama = ConvertNonStreamingResponse(req.Model, doc.RootElement);
                await JsonSerializer.SerializeAsync(context.Response.Body, ollama, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            upstreamResponse.Dispose();
        }
    }

    // ----------------------------------------------------------------------
    // Translation helpers
    // ----------------------------------------------------------------------

    private static object ConvertMessage(OllamaMessage m)
    {
        // Pass content through; ignore images for now (Copilot won't accept them anyway).
        var obj = new Dictionary<string, object?>
        {
            ["role"] = m.Role,
            ["content"] = m.Content ?? ""
        };
        if (m.ToolCalls is { Count: > 0 } tc) obj["tool_calls"] = tc;
        if (!string.IsNullOrEmpty(m.Name)) obj["name"] = m.Name;
        return obj;
    }

    private static async Task TranslateSseToNdjsonAsync(string model, Stream sseStream, Stream outputStream, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(sseStream);
        string? line;
        var lastFinishReason = "stop";
        var promptTokens = 0;
        var completionTokens = 0;

        while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
        {
            if (line.Length == 0) continue;
            if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

            var payload = line.AsSpan("data:".Length).Trim().ToString();
            if (payload == "[DONE]") break;
            if (payload.Length == 0) continue;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(payload); }
            catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;

                if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    if (usageEl.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pti)) promptTokens = pti;
                    if (usageEl.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cti)) completionTokens = cti;
                }

                if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                {
                    continue;
                }
                var choice = choices[0];

                string? deltaContent = null;
                List<JsonElement>? deltaToolCalls = null;
                if (choice.TryGetProperty("delta", out var deltaEl) && deltaEl.ValueKind == JsonValueKind.Object)
                {
                    if (deltaEl.TryGetProperty("content", out var cEl) && cEl.ValueKind == JsonValueKind.String)
                    {
                        deltaContent = cEl.GetString();
                    }
                    if (deltaEl.TryGetProperty("tool_calls", out var tcEl) && tcEl.ValueKind == JsonValueKind.Array)
                    {
                        deltaToolCalls = tcEl.EnumerateArray().Select(e => e.Clone()).ToList();
                    }
                }

                if (choice.TryGetProperty("finish_reason", out var frEl) && frEl.ValueKind == JsonValueKind.String)
                {
                    lastFinishReason = frEl.GetString() ?? "stop";
                }

                // Skip frames that have nothing to deliver.
                if (deltaContent is null && deltaToolCalls is null)
                {
                    continue;
                }

                var chunk = new Dictionary<string, object?>
                {
                    ["model"] = model,
                    ["created_at"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK"),
                    ["message"] = BuildMessage(deltaContent ?? "", deltaToolCalls),
                    ["done"] = false
                };
                await WriteNdjsonAsync(outputStream, chunk, cancellationToken).ConfigureAwait(false);
            }
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

    private static object BuildMessage(string content, List<JsonElement>? toolCalls)
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

    private static object ConvertNonStreamingResponse(string model, JsonElement openAi)
    {
        string content = "";
        var finishReason = "stop";
        var promptTokens = 0;
        var completionTokens = 0;

        if (openAi.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg) && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
            {
                content = c.GetString() ?? "";
            }
            if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                finishReason = fr.GetString() ?? "stop";
            }
        }
        if (openAi.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pti)) promptTokens = pti;
            if (usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cti)) completionTokens = cti;
        }

        return new
        {
            model,
            created_at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffffffK"),
            message = new { role = "assistant", content },
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
