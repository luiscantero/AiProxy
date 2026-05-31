using System.Diagnostics;
using AiProxy.Storage;
using Microsoft.Extensions.Logging;

namespace AiProxy.Auth.Copilot;

public sealed class CopilotAuthProvider : IAuthProvider
{
    public const string ProviderName = "copilot";

    private static readonly TimeSpan RefreshLeeway = TimeSpan.FromMinutes(2);
    private static readonly SemaphoreSlim RefreshLock = new(1, 1);

    private readonly DeviceFlowClient _deviceFlow;
    private readonly CopilotTokenClient _tokenClient;
    private readonly CopilotModelsClient _modelsClient;
    private readonly ITokenStore _store;
    private readonly ILogger<CopilotAuthProvider> _logger;

    public string Name => ProviderName;

    public CopilotAuthProvider(
        DeviceFlowClient deviceFlow,
        CopilotTokenClient tokenClient,
        CopilotModelsClient modelsClient,
        ITokenStore store,
        ILogger<CopilotAuthProvider> logger)
    {
        _deviceFlow = deviceFlow;
        _tokenClient = tokenClient;
        _modelsClient = modelsClient;
        _store = store;
        _logger = logger;
    }

    public async Task RunConnectAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _store.LoadAsync(ProviderName, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine("== AiProxy: Connect to GitHub Copilot ==");
        Console.WriteLine();

        // 1. Device flow
        var device = await _deviceFlow.RequestDeviceCodeAsync(cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  1. Open this URL in your browser:  {device.VerificationUri}");
        Console.WriteLine($"  2. Enter the code:                  {device.UserCode}");
        Console.WriteLine();
        Console.WriteLine("     (Browser will be opened automatically. Waiting for you to authorize...)");
        Console.WriteLine();

        TryOpenBrowser(device.VerificationUri);

        var ghToken = await _deviceFlow.PollForAccessTokenAsync(device, cancellationToken).ConfigureAwait(false);
        Console.WriteLine("  GitHub authorization granted.");

        // 2. Exchange for Copilot bearer
        var copilot = await _tokenClient.ExchangeAsync(ghToken, cancellationToken).ConfigureAwait(false);
        Console.WriteLine($"  Copilot token acquired (expires {copilot.ExpiresAt:u}).");
        if (!string.IsNullOrEmpty(copilot.ApiBaseUrl))
        {
            Console.WriteLine($"  Upstream API   : {copilot.ApiBaseUrl}");
        }
        Console.WriteLine();

        // 3. Fetch models
        var modelsResult = await _modelsClient.ListAsync(copilot.Token, copilot.ApiBaseUrl, cancellationToken).ConfigureAwait(false);
        var models = modelsResult.Models;

        if (models.Count == 0)
        {
            throw new InvalidOperationException("Upstream returned no usable models (after filtering for chat-capable / picker-enabled).");
        }

        Console.WriteLine("Available models:");
        for (var i = 0; i < models.Count; i++)
        {
            var m = models[i];
            var ctx = m.Capabilities?.Limits?.MaxContextWindowTokens;
            var ctxLabel = ctx is { } c ? $"  ctx={c}" : "";
            var label = string.IsNullOrEmpty(m.Name) ? m.Id : $"{m.Id}  ({m.Name})";
            Console.WriteLine($"  [{i + 1,2}] {label}{ctxLabel}");
        }
        Console.WriteLine();

        var defaultSelection = existing?.SelectedModels is { Count: > 0 } prev
            ? string.Join(",", prev.Select(id => (models.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.Id == id).i + 1)).Where(i => i > 0))
            : "";

        var prompt = string.IsNullOrEmpty(defaultSelection)
            ? "Select model(s) (e.g. 1,3 or * for all): "
            : $"Select model(s) (e.g. 1,3 or *) [default: {defaultSelection}]: ";

        IReadOnlyList<string> selected;
        while (true)
        {
            Console.Write(prompt);
            var input = Console.ReadLine() ?? "";
            input = input.Trim();
            if (input.Length == 0 && !string.IsNullOrEmpty(defaultSelection))
            {
                input = defaultSelection;
            }

            try
            {
                selected = ParseSelection(input, models);
                if (selected.Count > 0) break;
                Console.WriteLine("  No models selected. Try again.");
            }
            catch (FormatException ex)
            {
                Console.WriteLine($"  {ex.Message} Try again.");
            }
        }

        var modelInfos = models.ToDictionary(
            m => m.Id,
            m => new ModelInfo
            {
                Name = m.Name,
                Family = m.Capabilities?.Family,
                MaxContextWindowTokens = m.Capabilities?.Limits?.MaxContextWindowTokens,
                MaxOutputTokens = m.Capabilities?.Limits?.MaxOutputTokens
            },
            StringComparer.Ordinal);

        var state = new AuthState
        {
            Provider = ProviderName,
            GhOAuthToken = ghToken,
            CopilotToken = copilot.Token,
            CopilotTokenExpiresAt = copilot.ExpiresAt,
            UpstreamApiBaseUrl = copilot.ApiBaseUrl,
            SelectedModels = selected,
            ModelInfos = modelInfos
        };
        await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Saved auth state to: {_store.GetStoragePath(ProviderName)}");
        Console.WriteLine($"Selected models    : {string.Join(", ", selected)}");
        Console.WriteLine();
    }

    public async Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _store.LoadAsync(ProviderName, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        await _store.DeleteAsync(ProviderName, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(ProviderName, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException(
                        "No Copilot auth state found. Run 'AiProxy connect copilot' first.");

        if (!string.IsNullOrEmpty(state.CopilotToken)
            && state.CopilotTokenExpiresAt is { } expires
            && expires - DateTimeOffset.UtcNow > RefreshLeeway)
        {
            return state.CopilotToken;
        }

        await RefreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Re-check after acquiring the lock.
            state = await _store.LoadAsync(ProviderName, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Auth state vanished during refresh.");

            if (!string.IsNullOrEmpty(state.CopilotToken)
                && state.CopilotTokenExpiresAt is { } exp
                && exp - DateTimeOffset.UtcNow > RefreshLeeway)
            {
                return state.CopilotToken;
            }

            if (string.IsNullOrEmpty(state.GhOAuthToken))
            {
                throw new InvalidOperationException(
                    "Stored Copilot auth state is missing the GitHub OAuth token. Re-run 'AiProxy connect copilot'.");
            }

            _logger.LogInformation("Refreshing Copilot bearer token.");
            var refreshed = await _tokenClient.ExchangeAsync(state.GhOAuthToken, cancellationToken).ConfigureAwait(false);
            var updated = state with
            {
                CopilotToken = refreshed.Token,
                CopilotTokenExpiresAt = refreshed.ExpiresAt,
                UpstreamApiBaseUrl = refreshed.ApiBaseUrl ?? state.UpstreamApiBaseUrl
            };
            await _store.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
            return refreshed.Token;
        }
        finally
        {
            RefreshLock.Release();
        }
    }

    public async Task<IReadOnlyList<string>> GetSelectedModelsAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(ProviderName, cancellationToken).ConfigureAwait(false);
        return state?.SelectedModels ?? Array.Empty<string>();
    }

    public async Task<IReadOnlyDictionary<string, ModelInfo>> GetModelInfosAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(ProviderName, cancellationToken).ConfigureAwait(false);
        return state?.ModelInfos ?? new Dictionary<string, ModelInfo>();
    }

    public async Task<string?> GetUpstreamApiBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(ProviderName, cancellationToken).ConfigureAwait(false);
        return state?.UpstreamApiBaseUrl;
    }

    public async Task PrepareUpstreamRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        CopilotHeaders.Apply(request);
        request.Headers.TryAddWithoutValidation("OpenAI-Intent", "conversation-panel");
    }

    private static IReadOnlyList<string> ParseSelection(string input, IReadOnlyList<CopilotModelsClient.ModelEntry> models)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new FormatException("Empty selection.");
        }

        if (input.Trim() == "*")
        {
            return models.Select(m => m.Id).ToList();
        }

        var parts = input.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ids = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (!int.TryParse(part, out var idx))
            {
                throw new FormatException($"'{part}' is not a number.");
            }
            if (idx < 1 || idx > models.Count)
            {
                throw new FormatException($"Index {idx} is out of range (1..{models.Count}).");
            }
            var id = models[idx - 1].Id;
            if (!ids.Contains(id))
            {
                ids.Add(id);
            }
        }
        return ids;
    }

    private static void TryOpenBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            });
        }
        catch
        {
            // Best-effort only.
        }
    }
}
