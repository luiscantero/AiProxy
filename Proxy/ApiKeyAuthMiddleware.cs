using Microsoft.AspNetCore.Http;

namespace AiProxy.Proxy;

/// <summary>
/// Lightweight gate in front of all routed endpoints. The proxy is loopback-only by
/// default, so both surfaces (/api Ollama, /v1 OpenAI) are unauthenticated to keep
/// VS Code's built-in providers compatible. Also serves a tiny health response at /.
/// </summary>
public sealed class ApiKeyAuthMiddleware
{
    private readonly RequestDelegate _next;

    public ApiKeyAuthMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (HttpMethods.IsGet(context.Request.Method) && context.Request.Path == "/")
        {
            await context.Response.WriteAsync("AiProxy is running.\n");
            return;
        }

        await _next(context);
    }
}
