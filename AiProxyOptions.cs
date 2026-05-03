namespace AiProxy;

public sealed class AiProxyOptions
{
    public string ListenUrl { get; set; } = "http://127.0.0.1:11435";
    public string ProxyApiKey { get; set; } = "";
    public CopilotOptions Copilot { get; set; } = new();
    public ApiSurfaceOptions Apis { get; set; } = new();
}

public sealed class CopilotOptions
{
    public string ClientId { get; set; } = "Iv1.b507a08c87ecfe98";
    public string UpstreamBaseUrl { get; set; } = "https://api.githubcopilot.com";
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
