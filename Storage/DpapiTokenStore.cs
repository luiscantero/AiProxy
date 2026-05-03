using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiProxy.Storage;

public sealed class DpapiTokenStore : ITokenStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _baseDirectory;

    public DpapiTokenStore()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            throw new PlatformNotSupportedException(
                "DpapiTokenStore is Windows-only. AiProxy currently supports auth-token storage on Windows only.");
        }

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        _baseDirectory = Path.Combine(appData, "AiProxy");
        Directory.CreateDirectory(_baseDirectory);
    }

    public string GetStoragePath(string provider) =>
        Path.Combine(_baseDirectory, $"auth.{provider}.bin");

    public async Task<AuthState?> LoadAsync(string provider, CancellationToken cancellationToken = default)
    {
        var path = GetStoragePath(provider);
        if (!File.Exists(path))
        {
            return null;
        }

        var protectedBytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
#pragma warning disable CA1416 // Windows-only API; constructor enforces Windows at runtime.
        var clearBytes = ProtectedData.Unprotect(protectedBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        var json = Encoding.UTF8.GetString(clearBytes);
        return JsonSerializer.Deserialize<AuthState>(json, JsonOptions);
    }

    public async Task SaveAsync(AuthState state, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(state.Provider))
        {
            throw new ArgumentException("AuthState.Provider must be set.", nameof(state));
        }

        var json = JsonSerializer.Serialize(state, JsonOptions);
        var clearBytes = Encoding.UTF8.GetBytes(json);
#pragma warning disable CA1416 // Windows-only API; constructor enforces Windows at runtime.
        var protectedBytes = ProtectedData.Protect(clearBytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
#pragma warning restore CA1416
        var path = GetStoragePath(state.Provider);
        await File.WriteAllBytesAsync(path, protectedBytes, cancellationToken).ConfigureAwait(false);
    }

    public Task DeleteAsync(string provider, CancellationToken cancellationToken = default)
    {
        var path = GetStoragePath(provider);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
        return Task.CompletedTask;
    }
}
