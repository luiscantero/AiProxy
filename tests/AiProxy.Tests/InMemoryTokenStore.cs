using AiProxy.Storage;

namespace AiProxy.Tests;

/// <summary>
/// Non-encrypted, in-memory <see cref="ITokenStore"/> for unit tests (DpapiTokenStore is
/// Windows-only and writes to disk).
/// </summary>
internal sealed class InMemoryTokenStore : ITokenStore
{
    private readonly Dictionary<string, AuthState> _states = new(StringComparer.OrdinalIgnoreCase);

    public Task<AuthState?> LoadAsync(string provider, CancellationToken cancellationToken = default) =>
        Task.FromResult(_states.TryGetValue(provider, out var state) ? state : null);

    public Task SaveAsync(AuthState state, CancellationToken cancellationToken = default)
    {
        _states[state.Provider] = state;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string provider, CancellationToken cancellationToken = default)
    {
        _states.Remove(provider);
        return Task.CompletedTask;
    }

    public string GetStoragePath(string provider) => $"memory://{provider}";
}
