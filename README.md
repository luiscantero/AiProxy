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
  compression, and optional text-to-image rendering). *Cut token usage on large
  tool outputs, logs, or verbose prompts automatically.*
- **Model fallback / failover** — Transparently retry against another model on
  outages, rate limits, or transient errors, picked automatically from the models
  you have connected. *Ride out a provider outage by failing over from one vendor
  to another mid-request — with no model ids to maintain.*
- **Gearbox / model shifter** — Bind models to gear positions and shift the whole
  proxy onto one from a small browser UI. *Flip every request between Sonnet, Opus,
  Sol, or Neutral with a click — no editor round-trip.*
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

Edit [appsettings.json](appsettings.json) (or `appsettings.Development.json`).
Every setting is described by [appsettings.schema.json](appsettings.schema.json),
which is wired up in [.vscode/settings.json](.vscode/settings.json) — so VS Code
gives you completion, inline documentation, and squiggles on typos or invalid
values while you edit.

- `ListenUrl` — address the proxy binds to. Default `http://localhost:11434`
  (the Ollama port, so Ollama-aware tools work out of the box).
- `ProxyApiKey` — optional. If set, clients must send it as a bearer token.
  It can also be supplied as an environment variable instead of being stored in
  the file.
- `Copilot:ClientId` / `Copilot:UpstreamBaseUrl` — Copilot device-flow client
  and upstream API base. The defaults work for normal Copilot accounts.
- `Apis:Ollama` / `Apis:OpenAi` — toggle which API surfaces are exposed.
- `ModelPriorityHighToLow` — your models ranked **most powerful (and usually most
  expensive) first**. It is the one place that decides "which model is stronger",
  and every feature that has to choose between models reads it: the
  [gearbox](#gearbox-middleware) lays its automatic gears out along it (gear 1 the
  cheapest, the top gear the strongest) and
  [model fallback](#model-fallback-middleware) steps *down* it when a model fails,
  instead of escalating onto something pricier.

  Entries are matched **tolerantly**, so the short label a model is known by is
  enough — and a label survives a version bump, unlike a pinned id:

```jsonc
"ModelPriorityHighToLow": [ "Opus", "Sol", "Luna", "Terra", "Sonnet" ]
```

  An entry may be a short label (`Opus` ranks `claude-opus-5` — and still ranks
  `claude-opus-6` next year), a full model id (`claude-opus-5`), or a partial one
  (`sonnet-4.5`). One entry may match several models: that defines a *tier*, not
  an ambiguity. Where two entries both match, the more specific one wins, so
  listing `gpt-4o-mini` explicitly still outranks a looser `gpt-4o` above it.
  Models you leave out are not excluded — they simply carry no ranking, so listing
  your top few is enough. An entry that matches nothing you have connected is
  dropped at startup with a warning. Leave the list empty and both features behave
  exactly as they did before it existed.
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
services.AddSingleton<IChatMiddleware, PixelPressMiddleware>();
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
| [ModelFallbackMiddleware](Pipeline/Middlewares/ModelFallbackMiddleware.cs) | **Model fallback / failover** (opt-in). When a model is unavailable (provider outage, rate limit, transient `5xx`), it transparently retries the request against an alternative model — chosen automatically from the connected models (or from an explicit chain), and possibly on a different provider. See [Model fallback](#model-fallback-middleware) below.                          |
| [GearboxMiddleware](Pipeline/Middlewares/GearboxMiddleware.cs)             | **Manual model shifter** (opt-in). Binds models to gear positions and re-routes *every* request to whichever gear is engaged, flipped from a small browser UI. Neutral honors the client's own model choice. See [Gearbox](#gearbox-middleware) below.                                                                                                                                  |
| [PixelPressMiddleware](Pipeline/Middlewares/PixelPressMiddleware.cs)       | **Text-to-image token squeeze** (opt-in). Renders bulky prompt text into a dense PNG and swaps it in as an `image_url` part, so a **vision-capable** model reads it at a fixed image-token cost instead of per-character text tokens. Inspired by [pxpipe](https://github.com/teamchong/pxpipe). Lossy (the model OCRs the pixels), so off by default. See [PixelPress](#pixelpress-middleware) below. |

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

#### PixelPress middleware

Anthropic (and other vision models) charge a **fixed** number of tokens for an
image based on its pixel dimensions, not on how much text it depicts. Dense text
— code, JSON, a big system prompt — can be rasterized so a page of pixels costs
far fewer tokens than the same characters would as text. The
[PixelPressMiddleware](Pipeline/Middlewares/PixelPressMiddleware.cs) (inspired by
[pxpipe](https://github.com/teamchong/pxpipe)) renders qualifying prompt text
into a monospace PNG and swaps it into the message as an `image_url` content
part, so the upstream model reads the pixels instead of the text.

- **Inbound only** — selected roles (default: `system` and `user`) whose text is
  at least `MinCharacters` long are rendered to a PNG data URL. Shorter blocks are
  left as text, since images carry a fixed token floor. When `IncludeHint` is on,
  a short text part is prepended telling the model the image contains text to read.
- **Vision required, and lossy** — the model OCRs the rendered pixels, so exact
  strings (hashes, long ids) can be misread. Only enable it for a
  **vision-capable** upstream model, and expect some accuracy loss on dense text.

It runs after the text transforms (so it rasterizes whatever text remains) and is
**fail-open**: any rendering error leaves the request untouched. Rendering uses
[SkiaSharp](https://github.com/mono/SkiaSharp). Opt-in via `PixelPress.Enabled`:

```jsonc
"PixelPress": {
  "Enabled": true,
  "Roles": [ "system", "user" ], // which message roles to render
  "MinCharacters": 2000,          // skip text blocks shorter than this
  "FontSize": 14,                 // px; smaller is denser but harder to read
  "MaxColumns": 120,              // hard-wrap width in monospace characters
  "IncludeHint": true             // prepend a "read the text in the image" note
}
```

#### Model fallback middleware

When a provider has an outage (such as the global Anthropic model outage this is
designed to ride out), rate-limits you, or returns a transient `5xx`, the
[ModelFallbackMiddleware](Pipeline/Middlewares/ModelFallbackMiddleware.cs) keeps
the request alive by automatically retrying it against a **prioritized list of
alternative models** — transparently to the client.

You don't configure model ids for this. By default (`Mode: "Auto"`) the
alternatives are **derived at runtime** from the models you actually have
connected, so there is nothing in `appsettings.json` that can go stale when a
model is renamed, retired, or de-selected. For a failed model, a candidate has to
be able to serve *this* request:

- the request's own requirements are honored — image parts need a vision model, a
  `tools` array needs a tool-calling model;
- a model whose context window is known to be **smaller** than the failed one's is
  dropped, rather than trading an outage for a truncation error;
- survivors are ordered by [`ModelPriorityHighToLow`](#configuration) when you
  configure one: the request steps **down** it, nearest rung first, so an outage
  degrades gracefully instead of quietly escalating every request onto your
  priciest model — stronger models are kept as a last resort behind everything
  else;
- models the priority list says nothing about (and every model, when you have no
  list) fall back to the capability heuristic: **same family** first, then the
  **closest** (smallest sufficient) context window, then the order you selected
  them in — so failover lands on the nearest equivalent, not on the biggest or
  priciest model you own.

At most `MaxCandidates` alternatives are tried, and anything in `Exclude` is never
chosen automatically. The catalog is only read **after** a failure, so the happy
path costs nothing.

If you'd rather pin the order for a particular model, add a *chain*: an array of
models in priority order where the **first** entry is the model a client requests
and the rest are the alternatives to try, in order. A chain wins over Auto for its
primary model. A fallback model can live on a different provider; it is resolved
(and authenticated) exactly like a directly-requested model, so failover can cross
vendors.

- The upstream call **fails fast** — the terminal invoker validates the response
  status before exposing any chunks — so a fallback happens *before* a single
  byte has streamed to the client. The retry is invisible to the caller.
- Only **retryable** statuses trigger a fallback (`RetryStatusCodes`, default
  `408, 409, 429, 500, 502, 503, 504, 529`) plus transport-level failures.
  Genuine client errors (e.g. a `400`) are returned unchanged, never masked.
- If a fallback model isn't exposed by any connected provider it is **skipped**;
  if every candidate fails, the **last** upstream error is surfaced so the client
  still sees a real failure rather than an empty success.
- Every switch is logged as a single `WARNING` naming the abandoned model, the
  reason, and the model now serving the request.

It runs **innermost** (closest to the upstream call), so the outer prompt
transforms run only once; each fallback attempt just re-sends the
already-transformed request with a different model id. Opt-in via
`Fallback.Enabled`:

```jsonc
"Fallback": {
  "Enabled": true,
  // "Auto" (default) picks alternatives from the connected models.
  // "Chains" only fails over for models listed in Chains below.
  "Mode": "Auto",
  "MaxCandidates": 2,
  // Never fail over onto these automatically (e.g. the expensive ones).
  "Exclude": [ "claude-opus-5" ],
  // Upstream statuses that trigger a fallback (transport failures always do).
  "RetryStatusCodes": [ 408, 409, 429, 500, 502, 503, 504, 529 ],
  // Optional overrides. Leave empty to rely entirely on "Auto".
  "Chains": [
    {
      // Clients request "gpt-5.6-sol"; on failure try the next, then the next.
      "Models": [ "gpt-5.6-sol", "claude-sonnet-5.0", "gemini-3.1-pro" ]
    }
  ]
}
```

#### Gearbox middleware

Inspired by the ["Model Shift" gear-shifter idea](https://x.com/VaibhavSisinty/status/2072983741396582475),
the [GearboxMiddleware](Pipeline/Middlewares/GearboxMiddleware.cs) turns model
selection into a **manual transmission**. Each gear position is bound to a model,
and you flip between them from a small shifter UI in your browser. Every incoming
chat request is re-routed onto whichever gear is currently engaged — no matter
which model the client (e.g. VS Code) actually asked for.

Leave `Gears` **empty** and the shifter is built for you at startup: one gear per
connected model, up to `MaxAutoGears`. Like `Fallback`'s Auto mode, that leaves no
model ids in configuration to go stale. With a
[`ModelPriorityHighToLow`](#configuration) configured the shifter is laid out like
a real gearbox — the cap keeps the strongest models, and **gear 1 is the cheapest**
with the top gear the most powerful; without one, the connected order is used
as-is. Fill `Gears` in only when you want specific positions, ordering, or labels.

- **Neutral** (`N`, the default) is a pass-through: the client's own model choice
  is honored. Any gear with an empty `Model` behaves the same way — handy for a
  "default" position (e.g. `R`).
- Engaging a gear swaps the request's model **and** re-resolves the owning
  provider, so a gear can point at a model on *any* connected provider.
- It is **fail-open**: if the engaged gear names a model no connected provider
  exposes, the request is left on its original model instead of failing. Such a
  gear is dropped from the shifter at startup (see below), so this only happens
  if a model disappears while the proxy is running.
- It runs near the top of the pipeline (just after logging), so the chosen model
  flows through the rest of the transforms and the fallback stage.

Open the shifter at **`/gearbox`** (printed on startup). It talks to two tiny
routes — `GET /gearbox/state` and `POST /gearbox/shift` (`{ "position": "3" }`) —
which are only mapped when the gearbox is enabled. Because the engaged gear is
in-memory runtime state, shifting takes effect on the *next* request and resets
to `Selected` when the proxy restarts.

Prefer a window over a browser tab? [gearbox-ui](gearbox-ui/README.md) is a small
Rust/Tauri desktop shifter that drives the same two routes, with a pin-on-top
button and number-key shortcuts.

Opt-in via `Gearbox.Enabled`:

```jsonc
"Gearbox": {
  "Enabled": true,
  "Selected": "N",              // gear engaged at startup ("N" = Neutral / no override)
  "MaxAutoGears": 6,            // how many gears to build when "Gears" is empty
  "Gears": []                   // leave empty to derive the shifter from connected models
}
```

Or pin the layout yourself:

```jsonc
"Gears": [
  { "Position": "1", "Label": "Luna",    "Model": "gpt-5.6-luna" },
  { "Position": "2", "Label": "Flash",   "Model": "gemini-3.1-flash" },
  { "Position": "3", "Label": "Sonnet",  "Model": "claude-sonnet-5.0" },
  { "Position": "4", "Label": "Sol",     "Model": "gpt-5.6-sol" },
  { "Position": "5", "Label": "Opus",    "Model": "claude-opus-5" },
  { "Position": "R", "Label": "Default", "Model": "" }   // empty model = pass-through
]
```

> Any model you list must be exposed by a connected provider (they show up in
> `/v1/models`). The client still has to request a model the proxy exposes — the
> gearbox then redirects that request onto the engaged gear's model.

#### Startup model validation

Caveman, Model fallback, and Gearbox can all reference model ids in
configuration. At startup, once the proxy has catalogued the models exposed by
every connected provider, each enabled middleware checks its own configured
model(s) against that catalog. If a reference doesn't resolve (a typo, a retired
model, a provider that isn't connected, ...) the proxy prints a friendly
`WARNING` with details and degrades that middleware for the run — the rest of the
pipeline (and the proxy itself) starts normally.

How far it degrades is up to the middleware, and none of them take the whole
feature down over a single bad id any more:

- **Model fallback** *prunes* the offending id out of its chain. It disables
  itself only when nothing usable is left at all — which, with `Mode: "Auto"`,
  cannot happen, since Auto never names a model in configuration.
- **Gearbox** *removes* the offending gear from the shifter (and returns to
  Neutral if that gear was the one engaged). It disables itself only when no gear
  with a model survives.
- **Caveman** disables itself, since it has exactly one model to compress with and
  no meaningful way to carry on without it.
- **`ModelPriorityHighToLow`** is checked first, before any middleware, since the
  gearbox builds its automatic gears from it. Entries that match no connected
  model are *removed from the order*; the rest still rank.

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
