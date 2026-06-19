# AiProxy

A small local proxy that exposes your GitHub Copilot subscription — and any
OpenAI-compatible provider (OpenAI, OpenRouter, Groq, DeepSeek, ...) — through an
OpenAI- and Ollama-shaped HTTP API, so you can point tools like VS Code,
editors, or scripts at `http://localhost:11434` and bring your own AI models.

## Features

- **Use Copilot anywhere** — Surface your GitHub Copilot subscription behind a
  standard OpenAI/Ollama API. *Point an Ollama-only tool, a CLI script, or a
  third-party editor at Copilot without it knowing the difference.*
- **One endpoint, many providers** — Mix Copilot with any OpenAI-compatible
  upstream (OpenAI, OpenRouter, Groq, DeepSeek, xAI, Gemini, local runtimes).
  *Add a provider with config only, then switch models without touching the
  client.*
- **Unified model catalog** — Every connected provider's models are merged into
  one `/v1/models` and `/api/tags` list, routed automatically by model id.
  *Browse all your models in a single dropdown and let the proxy pick the right
  upstream.*
- **Encrypted local auth** — GitHub device-flow login and API keys are stored
  on-disk encrypted with Windows DPAPI. *Authenticate once with `connect`; no
  secrets in config files or environment variables.*
- **Token-saving pipeline** — Composable middlewares shrink prompts before they
  go upstream (cache alignment, JSON minification, log squashing, caveman
  compression). *Cut token usage on large tool outputs, logs, or verbose
  prompts automatically.*
- **Model fallback / failover** — Transparently retry against a prioritized list
  of alternative models on outages, rate limits, or transient errors. *Ride out
  a provider outage by failing over from one vendor to another mid-request.*
- **Extensible by design** — Drop in your own ASP.NET-style middleware to
  transform requests and responses. *Add custom logging, redaction, or prompt
  rewriting in a few lines.*

## Requirements

- **.NET 10 SDK** — the project targets `net10.0`.
- **Windows** — auth state is encrypted at rest with Windows DPAPI (see
  [Storage/DpapiTokenStore.cs](Storage/DpapiTokenStore.cs)).
- **A GitHub Copilot subscription** for the `copilot` provider, and/or an API
  key for any OpenAI-compatible provider you connect.

## Commands

```text
AiProxy                       Start proxy mode (default).
AiProxy connect [provider]    Run the connect workflow (default: copilot).
AiProxy models  [provider]    Re-select models for a connected provider (default: copilot).
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

To change which models a connected provider exposes without re-authenticating,
re-run just the model picker:

```pwsh
dotnet run -- models           # re-select Copilot models
dotnet run -- models openrouter # re-select models for another provider
```

## Configuration

Edit [appsettings.json](appsettings.json) (or `appsettings.Development.json`):

- `ListenUrl` — address the proxy binds to. Default `http://localhost:11434`
  (the Ollama port, so Ollama-aware tools work out of the box).
- `ProxyApiKey` — optional. If set, clients must send it as a bearer token.
- `Copilot:ClientId` / `Copilot:UpstreamBaseUrl` — Copilot device-flow client
  and upstream API base. The defaults work for normal Copilot accounts.
- `Apis:Ollama` / `Apis:OpenAi` — toggle which API surfaces are exposed.
- `OpenAiProviders` — a list of OpenAI-compatible upstreams to expose (OpenAI,
  OpenRouter, Groq, DeepSeek, xAI, Gemini's OpenAI endpoint, local runtimes,
  ...). Each entry needs a `Name` and `BaseUrl`; adding one is configuration
  only. Connect to it with `AiProxy connect <name>` to store an API key
  (encrypted) and pick models. A key can also be supplied inline via `ApiKey`
  for non-interactive setups (stored in plaintext, so prefer `connect`).

```jsonc
"OpenAiProviders": [
  { "Name": "openai",     "BaseUrl": "https://api.openai.com/v1" },
  { "Name": "openrouter", "BaseUrl": "https://openrouter.ai/api/v1" }
]
```

Models from every connected provider are merged into the same `/v1/models` and
`/api/tags` catalog, and chat requests are routed to the owning provider by the
requested model id.

Auth state (GitHub OAuth token, Copilot bearer, API keys, selected models) is
stored locally and encrypted with Windows DPAPI — see [Storage/DpapiTokenStore.cs](Storage/DpapiTokenStore.cs).

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

// Registration order = execution order (outermost first).
services.AddSingleton<IChatMiddleware, LoggingChatMiddleware>();
services.AddSingleton<IChatMiddleware, CacheAlignerMiddleware>();
services.AddSingleton<IChatMiddleware, JsonCrusherMiddleware>();
services.AddSingleton<IChatMiddleware, LogCompressorMiddleware>();
services.AddSingleton<IChatMiddleware, CavemanMiddleware>();
services.AddSingleton<IChatMiddleware, TokenCompressionMiddleware>(); // your middleware

// ModelFallback stays innermost so each retry re-sends the already-transformed
// request; register your own middleware before it (above) unless it must wrap
// the upstream call directly.
services.AddSingleton<IChatMiddleware, ModelFallbackMiddleware>();

services.AddSingleton<ChatPipeline>();
```

### Built-in token-saving middlewares

The pipeline ships with a set of inbound transforms inspired by
[Headroom](https://github.com/chopratejas/headroom). Each one shrinks the prompt
**before** it is sent to GitHub Copilot, and each is **fail-open**: any
unexpected error is logged at debug level and the original request is forwarded
unchanged, so a middleware can never break a request.

| Middleware                                                                 | What it does                                                                                                                                                                                                                                                                                                                                                                              |
| -------------------------------------------------------------------------- | ----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| [CacheAlignerMiddleware](Pipeline/Middlewares/CacheAlignerMiddleware.cs)   | Stabilizes the cacheable **system-prompt prefix**. Volatile tokens (dates, ISO timestamps, UUIDs, epoch seconds) in the system message cause a provider KV-cache miss on every call; they are rewritten to fixed placeholders (`<DATE>`, `<TIMESTAMP>`, `<UUID>`, `<EPOCH>`) so the prefix stays byte-stable. Only system messages are touched.                                           |
| [JsonCrusherMiddleware](Pipeline/Middlewares/JsonCrusherMiddleware.cs)     | Losslessly **minifies embedded JSON** (tool outputs, API responses, DB rows) found inside message content. It locates balanced JSON spans, re-serializes them compactly, and only replaces a span when the result is strictly shorter. No keys, nulls, or values are dropped.                                                                                                             |
| [LogCompressorMiddleware](Pipeline/Middlewares/LogCompressorMiddleware.cs) | **Squashes log blocks.** When content looks like logs, it collapses consecutive duplicate lines (ignoring volatile timestamps) and thins long runs of low-severity `TRACE`/`DEBUG`/`INFO` lines, while always preserving `WARN`/`ERROR`/`FATAL`/`CRITICAL` lines and stack traces.                                                                                                        |
| [CavemanMiddleware](Pipeline/Middlewares/CavemanMiddleware.cs)             | **LLM-driven natural-language compression** (opt-in). Delegates [caveman compression](https://github.com/wilpel/caveman-compression) to a configured model (typically a local Ollama) to strip grammar/filler from prompt content while preserving facts, then optionally expands caveman replies back to fluent prose. See [Caveman compression](#caveman-compression-middleware) below. |
| [ModelFallbackMiddleware](Pipeline/Middlewares/ModelFallbackMiddleware.cs) | **Model fallback / failover** (opt-in). When a model is unavailable (provider outage, rate limit, transient `5xx`), it transparently retries the request against a prioritized list of alternative models — which may live on different providers. See [Model fallback](#model-fallback-middleware) below.                                                                                |

These run after `LoggingChatMiddleware` (which stays outermost so it reports the
request as the client sent it). Because each is fail-open and only engages when
its content pattern is detected, the order among them is not critical;
`CacheAligner` is placed first so it sees the system prompt before any other
rewrite.

#### Caveman compression middleware

Unlike the mechanical transforms above, *caveman compression* is a
natural-language transform, so it can't be done with regexes — it needs a model.
The [CavemanMiddleware](Pipeline/Middlewares/CavemanMiddleware.cs) therefore
delegates the work to a second, configurable provider (the
[ICavemanTransformer](Pipeline/Middlewares/CavemanTransformer.cs) issues a plain
`/chat/completions` call against it). Point it at a cheap **local model** so the
compression doesn't cost more than it saves.

- **Inbound** — selected prompt messages (default: `user` role) are rewritten
  into terse caveman form before they reach the upstream model, cutting prompt
  tokens. The rewrite is only kept when it is actually shorter than the original.
- **Outbound** — optionally expands caveman text in the assistant reply back to
  fluent prose. Because that needs the whole message, the streamed response is
  buffered, expanded once, then re-emitted. Leave this **off** unless you also
  instruct the upstream to answer in caveman form (otherwise normal prose is
  needlessly round-tripped). Tool-call responses are never decompressed.

It is **fail-open**: a missing/misconfigured provider or any error leaves the
content untouched.

Configure it under `Caveman` in `appsettings.json` (the `Provider` must match a
registered Copilot or `OpenAiProviders` entry — e.g. a local Ollama added there):

```jsonc
"OpenAiProviders": [
  { "Name": "ollama", "BaseUrl": "http://localhost:11434/v1" }
],
"Caveman": {
  "Enabled": true,
  "Provider": "ollama",        // which registered provider performs the transform
  "Model": "llama3.1:8b",      // model the compressor provider should use
  "CompressRequests": true,    // caveman-compress prompts on the way upstream
  "DecompressResponses": false, // expand caveman replies on the way downstream
  "Roles": [ "user" ],         // which message roles to compress
  "MinCharacters": 400          // skip content shorter than this
}
```

#### Model fallback middleware

When a provider has an outage (such as the global Anthropic model outage this is
designed to ride out), rate-limits you, or returns a transient `5xx`, the
[ModelFallbackMiddleware](Pipeline/Middlewares/ModelFallbackMiddleware.cs) keeps
the request alive by automatically retrying it against a **prioritized list of
alternative models** — transparently to the client.

You configure one or more *chains*. Each chain is simply an array of models in
priority order: the **first** entry is the model a client requests, and the rest
are the alternatives to try, in order, when an attempt fails. A fallback model
can live on a different provider; it is resolved (and authenticated) exactly like
a directly-requested model, so a chain can fail over from one vendor to another.

- The upstream call **fails fast** — the terminal invoker validates the response
  status before exposing any chunks — so a fallback happens *before* a single
  byte has streamed to the client. The retry is invisible to the caller.
- Only **retryable** statuses trigger a fallback (`RetryStatusCodes`, default
  `408, 409, 429, 500, 502, 503, 504, 529`) plus transport-level failures.
  Genuine client errors (e.g. a `400`) are returned unchanged, never masked.
- If a configured fallback model isn't exposed by any connected provider it is
  **skipped**; if every candidate fails, the **last** upstream error is surfaced
  so the client still sees a real failure rather than an empty success.

It runs **innermost** (closest to the upstream call), so the outer prompt
transforms run only once; each fallback attempt just re-sends the
already-transformed request with a different model id. Opt-in via
`Fallback.Enabled`:

```jsonc
"Fallback": {
  "Enabled": true,
  // Upstream statuses that trigger a fallback (transport failures always do).
  "RetryStatusCodes": [ 408, 409, 429, 500, 502, 503, 504, 529 ],
  "Chains": [
    {
      // Clients request "claude-sonnet-4.6"; on failure try the next, then the next.
      "Models": [ "claude-sonnet-4.6", "gpt-5.5", "gemini-3.1-pro" ]
    }
  ]
}
```

#### Ideas for future middlewares

Other Headroom-style stages that fit this architecture:

- **CodeCompressor** — AST-aware compression of fenced code blocks.
- **CCR (Compress-Cache-Retrieve)** — store originals locally (we already have
  DPAPI storage) and replace compressed spans with markers the model can expand
  on demand via a tool/endpoint; this makes lossy squashers safe.
- **ConversationDelta** — for multi-turn chats, send only what changed since the
  previous turn instead of resending the whole history.
- **RelevanceSquasher** — statistically drop low-signal middle context.

## License

Released under the MIT License.
