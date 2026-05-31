namespace AiProxy.Pipeline;

/// <summary>
/// Represents the next component in the chat pipeline. Calling it runs the rest of the
/// pipeline (any inner middlewares plus the terminal upstream invoker).
/// </summary>
public delegate Task ChatMiddlewareDelegate(ChatPipelineContext context);

/// <summary>
/// A pluggable stage in the chat pipeline, analogous to an ASP.NET Core middleware.
///
/// Implementations can:
/// <list type="bullet">
///   <item>Inspect or rewrite <see cref="ChatPipelineContext.UpstreamRequest"/> before calling <paramref name="next"/>
///   (for example, compressing prompt tokens before they are sent to GitHub Copilot).</item>
///   <item>Wrap <see cref="ChatPipelineContext.ResponseChunks"/> after <paramref name="next"/> returns
///   (for example, decompressing the streamed response before it reaches the client).</item>
///   <item>Short-circuit by not calling <paramref name="next"/> at all.</item>
/// </list>
///
/// Register implementations in DI; they run in registration order (outermost first).
/// </summary>
public interface IChatMiddleware
{
    Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next);
}
