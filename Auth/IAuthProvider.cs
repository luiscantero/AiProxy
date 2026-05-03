using AiProxy.Storage;

namespace AiProxy.Auth;

public interface IAuthProvider
{
    string Name { get; }

    Task RunConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a fresh upstream access token, refreshing if needed.
    /// Throws InvalidOperationException if no auth state exists.
    /// </summary>
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the persisted models the user selected during connect, or empty if no state.
    /// </summary>
    Task<IReadOnlyList<string>> GetSelectedModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns metadata about the persisted models (context window, family, ...) keyed by id.
    /// May be empty if no auth state has been established yet.
    /// </summary>
    Task<IReadOnlyDictionary<string, ModelInfo>> GetModelInfosAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the upstream API base URL for this provider (e.g. the Copilot endpoints.api).
    /// May return null if no auth state has been established yet.
    /// </summary>
    Task<string?> GetUpstreamApiBaseUrlAsync(CancellationToken cancellationToken = default);
}
