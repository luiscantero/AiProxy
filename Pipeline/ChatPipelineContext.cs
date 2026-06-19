using System.Text.Json.Nodes;
using AiProxy.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace AiProxy.Pipeline;

/// <summary>
/// Carries everything a chat request needs as it flows through the middleware pipeline.
///
/// The flow mirrors ASP.NET Core middleware: each <see cref="IChatMiddleware"/> may mutate
/// <see cref="UpstreamRequest"/> on the way in (before calling <c>next</c>), and may wrap
/// <see cref="ResponseChunks"/> on the way out (after <c>next</c> returns). The terminal
/// component (<see cref="UpstreamChatInvoker"/>) sends the request to GitHub Copilot and
/// populates <see cref="ResponseChunks"/>.
/// </summary>
public sealed class ChatPipelineContext
{
    /// <summary>The underlying HTTP context for the downstream (VS Code) request.</summary>
    public required HttpContext Http { get; init; }

    /// <summary>Which wire format the downstream client used.</summary>
    public required ClientSurface Surface { get; init; }

    /// <summary>
    /// The model id the request currently targets. Initialized to the client-requested model
    /// (already validated against the allow-list); the fallback middleware may swap it to an
    /// alternative model when the primary is unavailable.
    /// </summary>
    public required string Model { get; set; }

    /// <summary>Whether the client asked for a streaming response.</summary>
    public required bool IsStreaming { get; init; }

    /// <summary>
    /// The OpenAI-shaped chat completion request that will be sent upstream. This is mutable:
    /// middlewares can rewrite messages (e.g. to compress prompts) before the terminal sends it.
    /// </summary>
    public required JsonObject UpstreamRequest { get; init; }

    /// <summary>
    /// The authenticated upstream provider that owns <see cref="Model"/>. The fallback middleware
    /// may swap it when retrying against an alternative model hosted by a different provider.
    /// </summary>
    public required IAuthProvider Provider { get; set; }

    /// <summary>A logger scoped to the pipeline.</summary>
    public required ILogger Logger { get; init; }

    /// <summary>
    /// The normalized upstream response stream. The terminal sets this; middlewares may wrap it
    /// after calling <c>next</c> (e.g. to decompress text on the way back to the client).
    /// </summary>
    public IAsyncEnumerable<ChatResponseChunk> ResponseChunks { get; set; } =
        AsyncEnumerable.Empty<ChatResponseChunk>();

    /// <summary>Free-form per-request state for middlewares to share data.</summary>
    public IDictionary<object, object?> Items { get; } = new Dictionary<object, object?>();

    /// <summary>Cancellation tied to the downstream request lifetime.</summary>
    public CancellationToken CancellationToken => Http.RequestAborted;
}

/// <summary>Small helper so middlewares can start from an empty response stream.</summary>
internal static class AsyncEnumerable
{
    public static async IAsyncEnumerable<T> Empty<T>()
    {
        await Task.CompletedTask;
        yield break;
    }
}
