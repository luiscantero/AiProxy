using System.Net.Http.Headers;
using AiProxy.Storage;

namespace AiProxy.Auth;

public interface IAuthProvider
{
    string Name { get; }

    Task RunConnectAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes any persisted auth state for this provider.
    /// Returns true if state existed and was removed; false if there was nothing to log out from.
    /// </summary>
    Task<bool> LogoutAsync(CancellationToken cancellationToken = default);

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

    /// <summary>
    /// Applies provider-specific authentication and headers to an outgoing upstream
    /// <c>/chat/completions</c> request. The default implementation sets a
    /// <c>Bearer</c> Authorization header from <see cref="GetAccessTokenAsync"/>; providers
    /// that need a different scheme or extra headers (e.g. GitHub Copilot's editor headers)
    /// override this. This is the single seam the terminal invoker uses, so adding a new
    /// provider never requires touching the invoker.
    /// </summary>
    async Task PrepareUpstreamRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }
}
