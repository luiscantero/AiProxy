namespace AiProxy.Auth.Copilot;

internal static class CopilotHeaders
{
    public const string EditorVersion = "AiProxy/1.0";
    public const string EditorPluginVersion = "AiProxy/1.0";
    public const string UserAgent = "AiProxy/1.0";
    public const string IntegrationId = "vscode-chat";

    /// <summary>
    /// Apply the standard Copilot editor headers required by the upstream API.
    /// </summary>
    public static void Apply(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("Editor-Version", EditorVersion);
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", EditorPluginVersion);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Copilot-Integration-Id", IntegrationId);
    }
}
