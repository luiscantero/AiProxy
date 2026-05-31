using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Auth;
using AiProxy.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiProxy.Proxy;

/// <summary>
/// OpenAI-compatible <c>/v1/chat/completions</c> surface.
///
/// This adapter only translates between the OpenAI wire format and the internal pipeline:
/// it parses the request, runs the <see cref="ChatPipeline"/> (logging, prompt transforms,
/// the upstream call, response transforms, ...), and serializes the normalized response back
/// into OpenAI SSE / JSON. All transformation logic lives in pipeline middlewares.
/// </summary>
public static class ChatCompletionsEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(
        HttpContext context,
        IEnumerable<IAuthProvider> providers,
        ChatPipeline pipeline,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AiProxy.Proxy.Chat");
        var cancellationToken = context.RequestAborted;

        // Parse the body into a mutable JSON object so middlewares can rewrite it. Because the
        // OpenAI surface is already OpenAI-shaped, the parsed body *is* the upstream request.
        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms, cancellationToken).ConfigureAwait(false);

        JsonObject upstreamRequest;
        try
        {
            upstreamRequest = JsonNode.Parse(ms.ToArray()) as JsonObject
                              ?? throw new JsonException("Request body must be a JSON object.");
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, $"Invalid JSON: {ex.Message}", "bad_request");
            return;
        }

        var requestedModel = upstreamRequest["model"]?.GetValue<string>();
        var isStream = upstreamRequest["stream"]?.GetValue<bool>() ?? false;

        if (string.IsNullOrEmpty(requestedModel))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Missing 'model' in request.", "bad_request");
            return;
        }

        var provider = await ProviderResolver.ResolveForModelAsync(providers, requestedModel, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"Model '{requestedModel}' is not exposed by this proxy.", "model_not_found");
            return;
        }

        var pipelineContext = new ChatPipelineContext
        {
            Http = context,
            Surface = ClientSurface.OpenAi,
            Model = requestedModel,
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
            await WriteErrorAsync(context, ex.StatusCode, ex.Body, "upstream_error");
            return;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream request failed.");
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, $"Upstream error: {ex.Message}", "bad_gateway");
            return;
        }

        if (isStream)
        {
            await WriteStreamingAsync(context, requestedModel, pipelineContext.ResponseChunks, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await WriteNonStreamingAsync(context, requestedModel, pipelineContext.ResponseChunks, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteStreamingAsync(
        HttpContext context,
        string model,
        IAsyncEnumerable<ChatResponseChunk> chunks,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "text/event-stream";

        var id = "chatcmpl-" + Guid.NewGuid().ToString("N");
        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int? promptTokens = null;
        int? completionTokens = null;

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.PromptTokens is { } pt) promptTokens = pt;
            if (chunk.CompletionTokens is { } ct) completionTokens = ct;

            if (chunk.ContentDelta is null && chunk.ToolCalls is null && chunk.FinishReason is null)
            {
                continue;
            }

            var delta = new JsonObject();
            if (chunk.ContentDelta is { } content) delta["content"] = content;
            if (chunk.ToolCalls is { } toolCalls) delta["tool_calls"] = ToJsonArray(toolCalls);

            var frame = new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion.chunk",
                ["created"] = created,
                ["model"] = model,
                ["choices"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["delta"] = delta,
                        ["finish_reason"] = chunk.FinishReason
                    }
                }
            };

            await WriteSseAsync(context.Response.Body, frame, cancellationToken).ConfigureAwait(false);
        }

        if (promptTokens is not null || completionTokens is not null)
        {
            var usageFrame = new JsonObject
            {
                ["id"] = id,
                ["object"] = "chat.completion.chunk",
                ["created"] = created,
                ["model"] = model,
                ["choices"] = new JsonArray(),
                ["usage"] = new JsonObject
                {
                    ["prompt_tokens"] = promptTokens ?? 0,
                    ["completion_tokens"] = completionTokens ?? 0,
                    ["total_tokens"] = (promptTokens ?? 0) + (completionTokens ?? 0)
                }
            };
            await WriteSseAsync(context.Response.Body, usageFrame, cancellationToken).ConfigureAwait(false);
        }

        await context.Response.Body.WriteAsync("data: [DONE]\n\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteNonStreamingAsync(
        HttpContext context,
        string model,
        IAsyncEnumerable<ChatResponseChunk> chunks,
        CancellationToken cancellationToken)
    {
        var content = new StringBuilder();
        var toolCalls = new List<JsonElement>();
        var finishReason = "stop";
        int? promptTokens = null;
        int? completionTokens = null;

        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.ContentDelta is { } c) content.Append(c);
            if (chunk.ToolCalls is { } tc) toolCalls.AddRange(tc);
            if (chunk.FinishReason is { } fr) finishReason = fr;
            if (chunk.PromptTokens is { } pt) promptTokens = pt;
            if (chunk.CompletionTokens is { } ct) completionTokens = ct;
        }

        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = content.ToString()
        };
        if (toolCalls.Count > 0) message["tool_calls"] = ToJsonArray(toolCalls);

        var response = new JsonObject
        {
            ["id"] = "chatcmpl-" + Guid.NewGuid().ToString("N"),
            ["object"] = "chat.completion",
            ["created"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            ["model"] = model,
            ["choices"] = new JsonArray
            {
                new JsonObject
                {
                    ["index"] = 0,
                    ["message"] = message,
                    ["finish_reason"] = finishReason
                }
            },
            ["usage"] = new JsonObject
            {
                ["prompt_tokens"] = promptTokens ?? 0,
                ["completion_tokens"] = completionTokens ?? 0,
                ["total_tokens"] = (promptTokens ?? 0) + (completionTokens ?? 0)
            }
        };

        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(response.ToJsonString(JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    private static JsonArray ToJsonArray(IReadOnlyList<JsonElement> elements)
    {
        var array = new JsonArray();
        foreach (var element in elements)
        {
            array.Add(JsonNode.Parse(element.GetRawText()));
        }
        return array;
    }

    private static async Task WriteSseAsync(Stream stream, JsonObject frame, CancellationToken cancellationToken)
    {
        var json = frame.ToJsonString(JsonOptions);
        var bytes = Encoding.UTF8.GetBytes($"data: {json}\n\n");
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message, string type)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new
        {
            error = new { message, type, code = statusCode }
        }, JsonOptions);
        await context.Response.WriteAsync(json);
    }
}
