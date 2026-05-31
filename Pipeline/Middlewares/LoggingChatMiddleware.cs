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

        await next(context).ConfigureAwait(false);

        // Wrap the response stream so we can observe it without buffering the whole thing.
        context.ResponseChunks = MeasureAsync(context, context.ResponseChunks, stopwatch);
    }

    private static async IAsyncEnumerable<ChatResponseChunk> MeasureAsync(
        ChatPipelineContext context,
        IAsyncEnumerable<ChatResponseChunk> source,
        Stopwatch stopwatch,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var characters = 0;
        string? finishReason = null;
        int? completionTokens = null;

        await foreach (var chunk in source.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            characters += chunk.ContentDelta?.Length ?? 0;
            finishReason ??= chunk.FinishReason;
            if (chunk.CompletionTokens is { } ct) completionTokens = ct;

            yield return chunk;
        }

        stopwatch.Stop();
        context.Logger.LogInformation(
            "Chat response: model={Model} chars={Characters} completionTokens={CompletionTokens} finish={Finish} elapsedMs={Elapsed}",
            context.Model, characters, completionTokens, finishReason ?? "?", stopwatch.ElapsedMilliseconds);
    }
}
