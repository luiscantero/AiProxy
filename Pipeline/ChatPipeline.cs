namespace AiProxy.Pipeline;

/// <summary>
/// Builds and runs the chat middleware pipeline. The registered <see cref="IChatMiddleware"/>
/// instances wrap the terminal <see cref="UpstreamChatInvoker"/>, in the same nesting model
/// ASP.NET Core uses for HTTP middleware.
/// </summary>
public sealed class ChatPipeline
{
    private readonly ChatMiddlewareDelegate _entry;

    public ChatPipeline(IEnumerable<IChatMiddleware> middlewares, UpstreamChatInvoker terminal)
    {
        // The terminal is the innermost component: it actually calls GitHub Copilot.
        ChatMiddlewareDelegate next = terminal.InvokeAsync;

        // Wrap from the inside out so the first-registered middleware ends up outermost.
        foreach (var middleware in middlewares.Reverse())
        {
            var captured = middleware;
            var inner = next;
            next = ctx => captured.InvokeAsync(ctx, inner);
        }

        _entry = next;
    }

    public Task InvokeAsync(ChatPipelineContext context) => _entry(context);
}
