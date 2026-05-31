# AiProxy

A small local proxy that exposes your GitHub Copilot subscription through an
OpenAI- and Ollama-shaped HTTP API, so you can point tools like VS Code,
editors, or scripts at `http://localhost:11434` and bring your own AI models.

## Commands

```text
AiProxy                       Start proxy mode (default).
AiProxy connect [provider]    Run the connect workflow (default: copilot).
AiProxy logout  [provider]    Remove stored auth state (default: copilot).
AiProxy help                  Show usage.
```

Typical first-run flow:

```pwsh
dotnet run -- connect      # GitHub device-flow login + pick the models you want
dotnet run                 # start the proxy on ListenUrl
```

To sign out and delete the stored token:

```pwsh
dotnet run -- logout
```

## Configuration

Edit [appsettings.json](appsettings.json) (or `appsettings.Development.json`):

- `ListenUrl` — address the proxy binds to. Default `http://localhost:11434`
  (the Ollama port, so Ollama-aware tools work out of the box).
- `ProxyApiKey` — optional. If set, clients must send it as a bearer token.
- `Copilot:ClientId` / `Copilot:UpstreamBaseUrl` — Copilot device-flow client
  and upstream API base. The defaults work for normal Copilot accounts.
- `Apis:Ollama` / `Apis:OpenAi` — toggle which API surfaces are exposed.

Auth state (GitHub OAuth token, Copilot bearer, selected models) is stored
locally and encrypted with Windows DPAPI — see [Storage/DpapiTokenStore.cs](Storage/DpapiTokenStore.cs).

## Chat middleware pipeline

Chat requests flow through a middleware pipeline modeled on
[ASP.NET Core middleware](https://learn.microsoft.com/aspnet/core/fundamentals/middleware/).
Both the OpenAI (`/v1/chat/completions`) and Ollama (`/api/chat`) endpoints are
thin adapters: they translate the incoming wire format into a normalized,
OpenAI-shaped request, run it through the pipeline, then serialize the
normalized response back into the client's format. All cross-cutting logic
(logging, prompt/response transforms) lives in middlewares — see the
[Pipeline/](Pipeline) folder.

A middleware can transform the request on the way **in** (before it reaches
GitHub Copilot) and the response on the way **back** (before it reaches the
client). This makes it possible to, for example, compress prompt tokens before
they are sent upstream and decompress the response on the way back.

```text
client → [middleware A] → [middleware B] → UpstreamChatInvoker → Copilot
client ← [middleware A] ← [middleware B] ← UpstreamChatInvoker ← Copilot
```

Middlewares run in registration order: the first registered is outermost, so
request transforms apply outer→inner and response transforms inner→outer.

### Writing a middleware

Implement [IChatMiddleware](Pipeline/IChatMiddleware.cs). The context exposes the
mutable OpenAI-shaped `UpstreamRequest` (a `JsonObject`) and the normalized
`ResponseChunks` stream:

```csharp
public sealed class TokenCompressionMiddleware : IChatMiddleware
{
    public async Task InvokeAsync(ChatPipelineContext ctx, ChatMiddlewareDelegate next)
    {
        // On the way IN: rewrite the prompt before it is sent to Copilot.
        if (ctx.UpstreamRequest["messages"] is JsonArray messages)
        {
            foreach (var msg in messages)
                msg!["content"] = Compress(msg["content"]!.GetValue<string>());
        }

        await next(ctx);

        // On the way BACK: wrap the response stream to transform it before the
        // client sees it (e.g. decompress streamed content).
        ctx.ResponseChunks = DecompressAsync(ctx.ResponseChunks);
    }

    private static string Compress(string text) => /* ... */ text;

    private static async IAsyncEnumerable<ChatResponseChunk> DecompressAsync(
        IAsyncEnumerable<ChatResponseChunk> source)
    {
        await foreach (var chunk in source)
        {
            if (chunk.ContentDelta is { } content)
                chunk.ContentDelta = Decompress(content);
            yield return chunk;
        }
    }

    private static string Decompress(string text) => /* ... */ text;
}
```

Because the response is an `IAsyncEnumerable<ChatResponseChunk>`, an outbound
transform can also buffer content across streaming chunks if it needs more than
a single delta at a time.

### Registering a middleware

Add it to the pipeline in `ServiceRegistration.Configure`
([Commands/ProxyCommand.cs](Commands/ProxyCommand.cs)). The shipped
[LoggingChatMiddleware](Pipeline/Middlewares/LoggingChatMiddleware.cs) is a
reference example that logs each request and the streamed response:

```csharp
services.AddSingleton<UpstreamChatInvoker>();
services.AddSingleton<IChatMiddleware, LoggingChatMiddleware>();
services.AddSingleton<IChatMiddleware, TokenCompressionMiddleware>(); // your middleware
services.AddSingleton<ChatPipeline>();
```
