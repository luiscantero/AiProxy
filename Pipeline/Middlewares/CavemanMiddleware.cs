using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// Caveman-compression middleware. Unlike the purely mechanical transforms (JsonCrusher,
/// LogCompressor), caveman compression is a natural-language transform, so it is delegated to a
/// configured LLM via <see cref="ICavemanTransformer"/> — typically a cheap local model such as
/// Ollama.
///
/// <para>Inbound</para> it rewrites selected prompt message content into terse "caveman" form
/// before the request reaches the (expensive) upstream model, reducing prompt tokens.
///
/// <para>Outbound</para> it can expand caveman text in the assistant response back into fluent
/// prose before it reaches the client. Because that is a whole-text transform, the streamed
/// response is buffered, expanded once, then re-emitted.
///
/// The middleware is fail-open: any error (or a missing/misconfigured provider) leaves the
/// original content untouched.
/// </summary>
public sealed class CavemanMiddleware : IChatMiddleware
{
    private readonly ICavemanTransformer _transformer;
    private readonly IOptions<AiProxyOptions> _options;

    public CavemanMiddleware(ICavemanTransformer transformer, IOptions<AiProxyOptions> options)
    {
        _transformer = transformer;
        _options = options;
    }

    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        var cfg = _options.Value.Caveman;

        if (!cfg.Enabled)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (cfg.CompressRequests)
        {
            await CompressRequestAsync(context, cfg).ConfigureAwait(false);
        }

        await next(context).ConfigureAwait(false);

        if (cfg.DecompressResponses)
        {
            context.ResponseChunks = DecompressResponseAsync(context, context.ResponseChunks, cfg);
        }
    }

    private async Task CompressRequestAsync(ChatPipelineContext context, CavemanOptions cfg)
    {
        if (context.UpstreamRequest["messages"] is not JsonArray messages)
        {
            return;
        }

        var roles = new HashSet<string>(cfg.Roles, StringComparer.OrdinalIgnoreCase);
        var totalSaved = 0;
        var compressedCount = 0;

        foreach (var msg in messages.OfType<JsonObject>())
        {
            var role = msg["role"]?.GetValue<string>();
            if (role is null || !roles.Contains(role))
            {
                continue;
            }

            // Case 1: content is a plain string.
            if (msg["content"] is JsonValue contentValue
                && contentValue.TryGetValue<string?>(out var text)
                && text is not null)
            {
                var (compressed, saved) = await TryCompressAsync(context, cfg, text).ConfigureAwait(false);
                if (saved > 0)
                {
                    msg["content"] = compressed;
                    totalSaved += saved;
                    compressedCount++;
                }
            }
            // Case 2: content is an array of parts.
            else if (msg["content"] is JsonArray parts)
            {
                foreach (var part in parts.OfType<JsonObject>())
                {
                    if (part["text"] is JsonValue partText
                        && partText.TryGetValue<string?>(out var partStr)
                        && partStr is not null)
                    {
                        var (compressed, saved) = await TryCompressAsync(context, cfg, partStr).ConfigureAwait(false);
                        if (saved > 0)
                        {
                            part["text"] = compressed;
                            totalSaved += saved;
                            compressedCount++;
                        }
                    }
                }
            }
        }

        if (totalSaved > 0)
        {
            context.Logger.LogInformation(
                "Caveman: compressed {Blocks} message block(s), saved {SavedChars} chars before upstream.",
                compressedCount, totalSaved);
        }
    }

    private async Task<(string text, int saved)> TryCompressAsync(
        ChatPipelineContext context,
        CavemanOptions cfg,
        string original)
    {
        if (original.Length < cfg.MinCharacters)
        {
            return (original, 0);
        }

        var compressed = await _transformer
            .TransformAsync(original, CavemanDirection.Compress, context.Logger, context.CancellationToken)
            .ConfigureAwait(false);

        // Only adopt the transform when it actually shrinks the content.
        if (compressed is null || compressed.Length >= original.Length)
        {
            return (original, 0);
        }

        return (compressed, original.Length - compressed.Length);
    }

    private async IAsyncEnumerable<ChatResponseChunk> DecompressResponseAsync(
        ChatPipelineContext context,
        IAsyncEnumerable<ChatResponseChunk> source,
        CavemanOptions cfg)
    {
        var cancellationToken = context.CancellationToken;

        var buffered = new List<ChatResponseChunk>();
        var contentBuilder = new StringBuilder();
        var hasToolCalls = false;

        await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            buffered.Add(chunk);
            if (chunk.ContentDelta is { Length: > 0 } delta)
            {
                contentBuilder.Append(delta);
            }
            if (chunk.ToolCalls is { Count: > 0 })
            {
                hasToolCalls = true;
            }
        }

        var fullText = contentBuilder.ToString();

        // Don't touch tool-call flows or content too short to be worth a round-trip.
        string? expanded = null;
        if (!hasToolCalls && fullText.Length >= cfg.MinCharacters)
        {
            expanded = await _transformer
                .TransformAsync(fullText, CavemanDirection.Decompress, context.Logger, cancellationToken)
                .ConfigureAwait(false);
        }

        if (expanded is null)
        {
            // Fail-open / skipped: replay the original stream untouched.
            foreach (var chunk in buffered)
            {
                yield return chunk;
            }
            yield break;
        }

        context.Logger.LogInformation(
            "Caveman: expanded assistant response from {Compressed} to {Expanded} chars.",
            fullText.Length, expanded.Length);

        // Emit the expanded text as a single content chunk, then replay the terminal signals
        // (finish reason + usage) so downstream surfaces still see completion metadata.
        yield return new ChatResponseChunk { ContentDelta = expanded };

        foreach (var chunk in buffered)
        {
            if (chunk.FinishReason is not null
                || chunk.PromptTokens is not null
                || chunk.CompletionTokens is not null)
            {
                yield return new ChatResponseChunk
                {
                    FinishReason = chunk.FinishReason,
                    PromptTokens = chunk.PromptTokens,
                    CompletionTokens = chunk.CompletionTokens,
                };
            }
        }
    }
}
