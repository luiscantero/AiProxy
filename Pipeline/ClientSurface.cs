namespace AiProxy.Pipeline;

/// <summary>
/// The wire format the downstream client (VS Code) used to talk to the proxy.
/// The pipeline itself is surface-agnostic and always works on an OpenAI-shaped
/// request/response; the surface only affects how we translate at the very edges.
/// </summary>
public enum ClientSurface
{
    /// <summary>OpenAI-compatible routes under <c>/v1</c>.</summary>
    OpenAi,

    /// <summary>Ollama-shaped routes under <c>/api</c>.</summary>
    Ollama
}
