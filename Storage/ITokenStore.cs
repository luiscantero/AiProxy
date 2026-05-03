namespace AiProxy.Storage;

public interface ITokenStore
{
    Task<AuthState?> LoadAsync(string provider, CancellationToken cancellationToken = default);
    Task SaveAsync(AuthState state, CancellationToken cancellationToken = default);
    Task DeleteAsync(string provider, CancellationToken cancellationToken = default);
    string GetStoragePath(string provider);
}
