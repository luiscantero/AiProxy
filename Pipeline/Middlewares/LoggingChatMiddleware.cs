using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// A reference middleware that logs each chat request and the streamed response size/timing.
///
/// It demonstrates the two extension points of the pipeline:
/// <list type="bullet">
///   <item><b>Inbound</b>: it reads <see cref="ChatPipelineContext.UpstreamRequest"/> before calling
///   <c>next</c> (here just to count messages — a real middleware could rewrite the prompt).</item>
///   <item><b>Outbound</b>: it wraps <see cref="ChatPipelineContext.ResponseChunks"/> after <c>next</c>
///   returns to observe the response as it streams back (here to count characters and tokens).</item>
/// </list>
/// </summary>
public sealed class LoggingChatMiddleware : IChatMiddleware
{
    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        var messageCount = (context.UpstreamRequest["messages"] as JsonArray)?.Count ?? 0;
        context.Logger.LogInformation(
            "Chat request: surface={Surface} model={Model} stream={Stream} messages={MessageCount}",
            context.Surface, context.Model, context.IsStreaming, messageCount);

        var stopwatch = Stopwatch.StartNew();

        // The model's advertised context window, so we can express actual usage as a percentage.
        // This is the post-compression input that really reaches the upstream provider — unlike
        // VS Code's gauge, which counts the un-compressed payload before it reaches the proxy.
        int? maxContextWindow = null;
        try
        {
            var infos = await context.Provider.GetModelInfosAsync(context.CancellationToken).ConfigureAwait(false);
            if (infos.TryGetValue(context.Model, out var info))
            {
                maxContextWindow = info.MaxContextWindowTokens;
            }
        }
        catch
        {
            // Model metadata is best-effort; never fail a chat over it.
        }

        await next(context).ConfigureAwait(false);

        // Wrap the response stream so we can observe it without buffering the whole thing.
        context.ResponseChunks = MeasureAsync(context, context.ResponseChunks, stopwatch, maxContextWindow);
    }

    private static async IAsyncEnumerable<ChatResponseChunk> MeasureAsync(
        ChatPipelineContext context,
        IAsyncEnumerable<ChatResponseChunk> source,
        Stopwatch stopwatch,
        int? maxContextWindow,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var characters = 0;
        string? finishReason = null;
        int? completionTokens = null;
        int? promptTokens = null;

        await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            characters += chunk.ContentDelta?.Length ?? 0;
            finishReason ??= chunk.FinishReason;
            if (chunk.CompletionTokens is { } ct) completionTokens = ct;
            if (chunk.PromptTokens is { } pt) promptTokens = pt;

            yield return chunk;
        }

        stopwatch.Stop();

        // Actual context window usage: prompt tokens the upstream counted (post-compression),
        // optionally as a percentage of the model's advertised window.
        string contextUsage;
        if (promptTokens is { } used && maxContextWindow is { } max && max > 0)
        {
            var percent = used * 100.0 / max;
            contextUsage = $"{used}/{max} ({percent:0.#}%)";
        }
        else if (promptTokens is { } usedOnly)
        {
            contextUsage = usedOnly.ToString();
        }
        else
        {
            contextUsage = "?";
        }

        context.Logger.LogInformation(
            "Chat response: model={Model} chars={Characters} promptTokens={PromptTokens} contextUsage={ContextUsage} completionTokens={CompletionTokens} finish={Finish} elapsedMs={Elapsed}",
            context.Model, characters, promptTokens, contextUsage, completionTokens, finishReason ?? "?", stopwatch.ElapsedMilliseconds);
    }
}
