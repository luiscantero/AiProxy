namespace AiProxy;

public sealed class AiProxyOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:11434";
    public string ProxyApiKey { get; set; } = "";
    public CopilotOptions Copilot { get; set; } = new();
    public ApiSurfaceOptions Apis { get; set; } = new();

    /// <summary>
    /// Caveman compression middleware: uses a second (typically local/cheap) LLM to
    /// losslessly "caveman-compress" prompt content before it is sent upstream, and to
    /// expand caveman text back to fluent prose on the way back to the client.
    /// </summary>
    public CavemanOptions Caveman { get; set; } = new();

    /// <summary>
    /// Model fallback middleware: when an upstream model is unavailable (outage, rate limit,
    /// 5xx), automatically retry the request against a prioritized list of alternative models.
    /// </summary>
    public FallbackOptions Fallback { get; set; } = new();

    /// <summary>
    /// Gearbox middleware: a manual "model shifter". The user maps models to gear positions and
    /// flips between them from a small web UI; every incoming chat request is transparently
    /// re-routed to whichever model is currently "in gear" (Neutral leaves the request untouched).
    /// </summary>
    public GearboxOptions Gearbox { get; set; } = new();

    /// <summary>
    /// PixelPress middleware: renders bulky prompt text into a PNG image so vision-capable models
    /// read it at a (fixed) image-token cost instead of paying per-character text tokens. Inspired
    /// by pxpipe. Lossy and vision-only, so it is disabled by default.
    /// </summary>
    public PixelPressOptions PixelPress { get; set; } = new();

    /// <summary>
    /// Publishes reasoning models once per thinking effort, as <c>&lt;model&gt;:&lt;level&gt;</c>
    /// variants, so the effort can be picked from any client's plain model list.
    /// </summary>
    public ReasoningEffortOptions ReasoningEffort { get; set; } = new();

    /// <summary>
    /// OpenAI-compatible upstreams to expose (OpenAI, OpenRouter, Groq, DeepSeek, Gemini's
    /// OpenAI endpoint, local runtimes, ...). Each entry becomes its own auth provider that
    /// can be connected with <c>AiProxy connect &lt;name&gt;</c>. Adding a new provider is
    /// configuration-only — no code changes required.
    /// </summary>
    public List<OpenAiCompatibleProviderOptions> OpenAiProviders { get; set; } = new();

    /// <summary>
    /// Your models ranked from most powerful (and typically most expensive) to least, shared by
    /// every feature that has to choose between models: the gearbox lays its automatic gears out
    /// along it (gear 1 the cheapest, the top gear the strongest) and fallback steps <i>down</i>
    /// it when a model fails, instead of escalating onto something pricier.
    ///
    /// <para>
    /// Entries are matched tolerantly, so the short label a model is known by is enough:
    /// "Opus" ranks <c>claude-opus-5</c> (and still ranks <c>claude-opus-6</c> after a version
    /// bump). A full model id or a partial one such as "sonnet-4.5" also works. Models you leave
    /// out are not excluded — they simply carry no ranking, so listing your top few is enough.
    /// Leave this empty to keep the previous behavior: gears in connected order, and fallback
    /// ranked purely by the capability heuristic.
    /// </para>
    /// </summary>
    public List<string> ModelPriorityHighToLow { get; set; } = new();
}

public sealed class CopilotOptions
{
    public string ClientId { get; set; } = "Iv1.b507a08c87ecfe98";
    public string UpstreamBaseUrl { get; set; } = "https://api.githubcopilot.com";
}

/// <summary>
/// A single OpenAI-compatible upstream. Authentication is a bearer API key.
/// </summary>
public sealed class OpenAiCompatibleProviderOptions
{
    /// <summary>Provider id used on the CLI and for stored state, e.g. "openai" or "openrouter".</summary>
    public string Name { get; set; } = "";

    /// <summary>OpenAI-compatible base URL including any version segment, e.g. "https://api.openai.com/v1".</summary>
    public string BaseUrl { get; set; } = "";

    /// <summary>
    /// Optional API key. Prefer running <c>AiProxy connect &lt;name&gt;</c> (stored encrypted)
    /// over putting a key here in plaintext; this is only a fallback for non-interactive setups.
    /// </summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Optional default model allow-list, used as the pre-filled selection during connect.</summary>
    public List<string> Models { get; set; } = new();
}

/// <summary>
/// Configuration for the caveman-compression middleware. The middleware delegates the actual
/// compression/decompression to a configured LLM (typically a local Ollama model) so the
/// transform can run on any natural language. Disabled by default.
/// </summary>
public sealed class CavemanOptions
{
    /// <summary>Master switch. When false the middleware passes every request through untouched.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Name of a registered provider (a Copilot or OpenAiProviders entry, e.g. "ollama") whose
    /// base URL + credentials are used to call the compression model. Should generally be a cheap,
    /// local model so compression does not cost more than it saves.
    /// </summary>
    public string Provider { get; set; } = "";

    /// <summary>Model id the compression provider should use for the transform.</summary>
    public string Model { get; set; } = "";

    /// <summary>Compress matching prompt message content on the way upstream. Default true.</summary>
    public bool CompressRequests { get; set; } = true;

    /// <summary>
    /// Expand caveman text in the assistant response on the way back to the client. Default false:
    /// only enable this when the upstream is instructed to answer in caveman form, otherwise normal
    /// prose would be needlessly round-tripped through the model.
    /// </summary>
    public bool DecompressResponses { get; set; }

    /// <summary>Which message roles to compress inbound. Default: just "user".</summary>
    public List<string> Roles { get; set; } = new() { "user" };

    /// <summary>
    /// Skip content shorter than this many characters. Short content rarely compresses enough to
    /// justify an extra LLM round-trip. Default 400.
    /// </summary>
    public int MinCharacters { get; set; } = 400;
}

/// <summary>
/// Configuration for the PixelPress middleware. Bulky prompt text is rendered into a dense PNG
/// image and swapped in as an <c>image_url</c> content part, so a vision-capable upstream model
/// reads it at a fixed image-token cost instead of paying per-character text tokens. The transform
/// is inbound-only, lossy (the model OCRs the pixels), and requires a vision model, so it is
/// disabled by default and fail-open.
/// </summary>
public sealed class PixelPressOptions
{
    /// <summary>Master switch. When false the middleware passes every request through untouched.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Which message roles to render. Bulky, mostly-static content (the system prompt and tool
    /// documentation) yields the biggest savings. Default: "system" and "user".
    /// </summary>
    public List<string> Roles { get; set; } = new() { "system", "user" };

    /// <summary>
    /// Only render text blocks at least this long. Images have a fixed token floor, so short text
    /// is cheaper left as-is. Default 2000.
    /// </summary>
    public int MinCharacters { get; set; } = 2000;

    /// <summary>Font size in pixels used to rasterize the text. Smaller is denser (fewer image
    /// tokens) but harder for the model to read reliably. Default 14.</summary>
    public int FontSize { get; set; } = 14;

    /// <summary>Hard-wrap width in monospace characters per line. Default 120.</summary>
    public int MaxColumns { get; set; } = 120;

    /// <summary>
    /// Prepend a short text instruction telling the model the image contains rendered text to read.
    /// Default true.
    /// </summary>
    public bool IncludeHint { get; set; } = true;
}

/// <summary>
/// Configuration for reasoning-effort model variants. Clients such as VS Code's Ollama provider
/// offer no UI for thinking effort, so each level a model accepts is published as its own model id
/// (<c>gpt-5.6-sol:high</c>) and translated back into <c>reasoning_effort</c> upstream. Which
/// models have levels, and which levels those are, is read from the upstream model catalog, so
/// there is nothing to keep in step here.
/// </summary>
public sealed class ReasoningEffortOptions
{
    /// <summary>Master switch. When false only the plain model ids are published.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Optional filter over the levels a model advertises, so the picker is not flooded with every
    /// gradation. This is a preference, not a mapping: it is intersected with what each model
    /// actually accepts, so a level a model does not have is simply skipped rather than sent and
    /// rejected, and there is nothing to keep in step as models change. Empty publishes them all.
    /// <para>
    /// Only publishing is filtered. A request naming any level the model really accepts is still
    /// honoured, so a hidden level stays reachable by hand.
    /// </para>
    /// </summary>
    public List<string> Levels { get; set; } = new();
}

/// <summary>
/// Configuration for the model fallback middleware. When a request to a primary model fails with
/// a retryable upstream error (outage, rate limit, 5xx), the middleware re-issues the same request
/// against an alternative model — transparently to the client. Disabled by default.
/// </summary>
public sealed class FallbackOptions
{
    /// <summary>Master switch. When false the middleware passes every request through untouched.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How alternatives are chosen for a model that has no explicit <see cref="Chains"/> entry.
    /// Defaults to <see cref="FallbackMode.Auto"/>, which needs no model ids in configuration at all.
    /// </summary>
    public FallbackMode Mode { get; set; } = FallbackMode.Auto;

    /// <summary>
    /// Maximum number of alternatives tried in <see cref="FallbackMode.Auto"/> mode (the primary
    /// attempt is not counted). Keeps a bad outage from walking the entire model catalog.
    /// </summary>
    public int MaxCandidates { get; set; } = 2;

    /// <summary>
    /// Models that <see cref="FallbackMode.Auto"/> must never fail over onto — typically the
    /// expensive ones you only want to use when you ask for them explicitly. Ignored by
    /// <see cref="Chains"/>, which is always taken at face value.
    /// </summary>
    public List<string> Exclude { get; set; } = new();

    /// <summary>
    /// Optional explicit overrides. Each chain lists models in priority order: the first entry is
    /// the model a client requests, and the remaining entries are the alternatives to try, in
    /// order, when an attempt fails. A chain wins over <see cref="FallbackMode.Auto"/> for its
    /// primary model. A fallback model may live on a different provider; it is resolved the same
    /// way a directly-requested model is.
    /// </summary>
    public List<FallbackChain> Chains { get; set; } = new();

    /// <summary>
    /// Upstream HTTP status codes that trigger a fallback. Any other status (e.g. 400 for a
    /// malformed request) is returned to the client unchanged. Defaults to 429 and 5xx.
    /// </summary>
    public List<int> RetryStatusCodes { get; set; } = new() { 408, 409, 429, 500, 502, 503, 504, 529 };
}

/// <summary>
/// How the fallback middleware picks alternatives for a model without an explicit chain.
/// </summary>
public enum FallbackMode
{
    /// <summary>
    /// Derive alternatives from the models actually connected right now, ranked by how well they
    /// substitute for the failed one (same family, big enough context window, and the capabilities
    /// the request needs). Requires no model ids in configuration, so it cannot go stale.
    /// </summary>
    Auto,

    /// <summary>
    /// Only fail over for models listed in <see cref="FallbackOptions.Chains"/>; every other model
    /// is passed through untouched.
    /// </summary>
    Chains
}

/// <summary>
/// A single prioritized list of models. <see cref="Models"/>[0] is the model clients request;
/// the rest are fallbacks tried in order when an attempt fails.
/// </summary>
public sealed class FallbackChain
{
    /// <summary>Models in priority order. The first is the requested model; the rest are fallbacks.</summary>
    public List<string> Models { get; set; } = new();
}


/// <summary>
/// Configuration for the gearbox ("model shift") middleware. Like the gear shifter of a manual
/// car, each gear position is bound to a model; the user shifts between them from a small web UI
/// (served at <c>/gearbox</c>) and every chat request is transparently re-routed to whichever
/// model is currently in gear. The <c>Neutral</c> position (and any gear with an empty
/// <see cref="GearOptions.Model"/>) is a pass-through: the client's own model choice is honored.
/// Disabled by default.
/// </summary>
public sealed class GearboxOptions
{
    /// <summary>Master switch. When false the middleware passes every request through untouched
    /// and the web UI endpoints are not mapped.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// The gear the proxy starts in, matched against <see cref="GearOptions.Position"/>. The
    /// special value <c>"N"</c> (case-insensitive) means Neutral — no override. Defaults to Neutral.
    /// </summary>
    public string Selected { get; set; } = "N";

    /// <summary>
    /// The gears available on the shifter, in display order. Each binds a position (e.g. "1".."6"
    /// or "R") to the model engaged when that gear is selected. Leave this <b>empty</b> to have the
    /// shifter built automatically at startup from the models you have connected — then there are
    /// no model ids here to go stale.
    /// </summary>
    public List<GearOptions> Gears { get; set; } = new();

    /// <summary>
    /// How many gears to generate when <see cref="Gears"/> is left empty. The shifter UI lays the
    /// gears out in two rows, so a handful keeps it readable. Default 6.
    /// </summary>
    public int MaxAutoGears { get; set; } = 6;
}

/// <summary>
/// A single gear position on the <see cref="GearboxOptions"/> shifter.
/// </summary>
public sealed class GearOptions
{
    /// <summary>Short position label shown on the shifter, e.g. "1", "2", ... "R".</summary>
    public string Position { get; set; } = "";

    /// <summary>Human-friendly name for the gear, e.g. "Sonnet" or "Opus". Optional.</summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// The model engaged when this gear is selected. Must be a model exposed by a connected
    /// provider. An empty value makes the gear a pass-through (equivalent to Neutral).
    /// </summary>
    public string Model { get; set; } = "";
}


/// <summary>
/// Toggles for the wire-format surfaces the proxy exposes.
/// </summary>
public sealed class ApiSurfaceOptions
{
    /// <summary>Ollama-shaped routes under /api (used by VS Code's Ollama provider).</summary>
    public bool Ollama { get; set; } = true;

    /// <summary>OpenAI-compatible routes under /v1 (for curl/scripts/other clients).</summary>
    public bool OpenAi { get; set; } = true;
}
