namespace AiProxy;

public sealed class AiProxyOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:11435";
    public string ProxyApiKey { get; set; } = "";
    public CopilotOptions Copilot { get; set; } = new();
    public ApiSurfaceOptions Apis { get; set; } = new();

    /// <summary>
    /// OpenAI-compatible upstreams to expose (OpenAI, OpenRouter, Groq, DeepSeek, Gemini's
    /// OpenAI endpoint, local runtimes, ...). Each entry becomes its own auth provider that
    /// can be connected with <c>AiProxy connect &lt;name&gt;</c>. Adding a new provider is
    /// configuration-only — no code changes required.
    /// </summary>
    public List<OpenAiCompatibleProviderOptions> OpenAiProviders { get; set; } = new();
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
/// Toggles for the wire-format surfaces the proxy exposes.
/// </summary>
public sealed class ApiSurfaceOptions
{
    /// <summary>Ollama-shaped routes under /api (used by VS Code's Ollama provider).</summary>
    public bool Ollama { get; set; } = true;

    /// <summary>OpenAI-compatible routes under /v1 (for curl/scripts/other clients).</summary>
    public bool OpenAi { get; set; } = true;
}
