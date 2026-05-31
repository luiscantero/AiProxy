namespace AiProxy.Storage;

public sealed record AuthState
{
    public string Provider { get; init; } = "";
    public string? GhOAuthToken { get; init; }
    public string? CopilotToken { get; init; }
    public DateTimeOffset? CopilotTokenExpiresAt { get; init; }

    /// <summary>
    /// API key for simple bearer-token providers (OpenAI-compatible: OpenAI, OpenRouter,
    /// Groq, DeepSeek, ...). Stored encrypted alongside the other state.
    /// </summary>
    public string? ApiKey { get; init; }
    /// <summary>
    /// Upstream API base URL returned by the Copilot token exchange (endpoints.api).
    /// Differs by plan: individual / business / enterprise.
    /// </summary>
    public string? UpstreamApiBaseUrl { get; init; }
    public IReadOnlyList<string> SelectedModels { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Per-model metadata captured from the upstream models response. Used by the
    /// Ollama /api/show endpoint to advertise context window etc. so VS Code can render
    /// the correct token usage gauge. Indexed by model id.
    /// </summary>
    public IReadOnlyDictionary<string, ModelInfo> ModelInfos { get; init; }
        = new Dictionary<string, ModelInfo>();
}

public sealed record ModelInfo
{
    public string? Name { get; init; }
    public string? Family { get; init; }
    public int? MaxContextWindowTokens { get; init; }
    public int? MaxOutputTokens { get; init; }
}
