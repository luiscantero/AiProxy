using System.Net.Http.Headers;
using AiProxy.Storage;
using Microsoft.Extensions.Logging;

namespace AiProxy.Auth.OpenAiCompatible;

/// <summary>
/// A bring-your-own-key provider for any OpenAI-compatible upstream (OpenAI, OpenRouter,
/// Groq, DeepSeek, xAI, Gemini's OpenAI endpoint, local runtimes, ...). One instance is
/// created per configured <see cref="OpenAiCompatibleProviderOptions"/>; the proxy can run
/// several side by side. Because the upstream already speaks the OpenAI wire format, this
/// provider needs no request/response translation — only an API key and a base URL.
/// </summary>
public sealed class OpenAiCompatibleAuthProvider : IAuthProvider
{
    private readonly OpenAiCompatibleProviderOptions _config;
    private readonly ITokenStore _store;
    private readonly OpenAiCompatibleModelsClient _modelsClient;
    private readonly ILogger _logger;

    public OpenAiCompatibleAuthProvider(
        OpenAiCompatibleProviderOptions config,
        ITokenStore store,
        OpenAiCompatibleModelsClient modelsClient,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(config.Name))
        {
            throw new ArgumentException("OpenAI-compatible provider requires a Name.", nameof(config));
        }
        if (string.IsNullOrWhiteSpace(config.BaseUrl))
        {
            throw new ArgumentException($"OpenAI-compatible provider '{config.Name}' requires a BaseUrl.", nameof(config));
        }

        _config = config;
        _store = store;
        _modelsClient = modelsClient;
        _logger = logger;
    }

    public string Name => _config.Name;

    public async Task RunConnectAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _store.LoadAsync(Name, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"== AiProxy: Connect to {Name} ({_config.BaseUrl}) ==");
        Console.WriteLine();

        var defaultKey = existing?.ApiKey ?? (string.IsNullOrEmpty(_config.ApiKey) ? null : _config.ApiKey);
        string apiKey;
        while (true)
        {
            var prompt = defaultKey is null
                ? "  Enter API key: "
                : "  Enter API key [press Enter to keep the existing key]: ";
            Console.Write(prompt);
            var entered = ReadSecret();
            if (string.IsNullOrEmpty(entered) && defaultKey is not null)
            {
                apiKey = defaultKey;
                break;
            }
            if (!string.IsNullOrWhiteSpace(entered))
            {
                apiKey = entered.Trim();
                break;
            }
            Console.WriteLine("  An API key is required. Try again.");
        }
        Console.WriteLine();

        Console.WriteLine("  Fetching available models...");
        var models = await _modelsClient.ListAsync(_config.BaseUrl, apiKey, cancellationToken).ConfigureAwait(false);
        if (models.Count == 0)
        {
            throw new InvalidOperationException("Upstream returned no models.");
        }

        await SelectModelsAndSaveAsync(apiKey, models, existing, cancellationToken).ConfigureAwait(false);
    }

    public async Task RunSelectModelsAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _store.LoadAsync(Name, cancellationToken).ConfigureAwait(false);
        var apiKey = existing?.ApiKey is { Length: > 0 } stored
            ? stored
            : (string.IsNullOrEmpty(_config.ApiKey) ? null : _config.ApiKey);

        if (apiKey is null)
        {
            throw new InvalidOperationException(
                $"No API key for '{Name}'. Run 'AiProxy connect {Name}' first.");
        }

        Console.WriteLine();
        Console.WriteLine($"== AiProxy: Select {Name} models ({_config.BaseUrl}) ==");
        Console.WriteLine();

        Console.WriteLine("  Fetching available models...");
        var models = await _modelsClient.ListAsync(_config.BaseUrl, apiKey, cancellationToken).ConfigureAwait(false);
        if (models.Count == 0)
        {
            throw new InvalidOperationException("Upstream returned no models.");
        }

        await SelectModelsAndSaveAsync(apiKey, models, existing, cancellationToken).ConfigureAwait(false);
    }

    private async Task SelectModelsAndSaveAsync(
        string apiKey,
        IReadOnlyList<OpenAiCompatibleModelsClient.ModelEntry> models,
        AuthState? existing,
        CancellationToken cancellationToken)
    {
        Console.WriteLine();
        Console.WriteLine("Available models:");
        for (var i = 0; i < models.Count; i++)
        {
            var m = models[i];
            var ctxLabel = m.ContextLength is { } c ? $"  ctx={c}" : "";
            Console.WriteLine($"  [{i + 1,3}] {m.Id}{ctxLabel}");
        }
        Console.WriteLine();

        var previous = existing?.SelectedModels is { Count: > 0 } prev
            ? prev
            : _config.Models;
        var defaultSelection = previous.Count > 0
            ? string.Join(",", previous
                .Select(id => models.Select((m, i) => (m, i)).FirstOrDefault(x => x.m.Id == id).i + 1)
                .Where(i => i > 0))
            : "";

        var selectionPrompt = string.IsNullOrEmpty(defaultSelection)
            ? "Select model(s) (e.g. 1,3 or * for all): "
            : $"Select model(s) (e.g. 1,3 or *) [default: {defaultSelection}]: ";

        IReadOnlyList<string> selected;
        while (true)
        {
            Console.Write(selectionPrompt);
            var input = (Console.ReadLine() ?? "").Trim();
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
                Family = Name,
                MaxContextWindowTokens = m.ContextLength
            },
            StringComparer.Ordinal);

        var state = new AuthState
        {
            Provider = Name,
            ApiKey = apiKey,
            UpstreamApiBaseUrl = _config.BaseUrl,
            SelectedModels = selected,
            ModelInfos = modelInfos
        };
        await _store.SaveAsync(state, cancellationToken).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"Saved auth state to: {_store.GetStoragePath(Name)}");
        Console.WriteLine($"Selected models    : {string.Join(", ", selected)}");
        Console.WriteLine();
    }

    public async Task<bool> LogoutAsync(CancellationToken cancellationToken = default)
    {
        var existing = await _store.LoadAsync(Name, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            return false;
        }

        await _store.DeleteAsync(Name, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(Name, cancellationToken).ConfigureAwait(false);
        var key = state?.ApiKey is { Length: > 0 } stored
            ? stored
            : (string.IsNullOrEmpty(_config.ApiKey) ? null : _config.ApiKey);

        return key ?? throw new InvalidOperationException(
            $"No API key for '{Name}'. Run 'AiProxy connect {Name}' first (or set OpenAiProviders ApiKey).");
    }

    public async Task<IReadOnlyList<string>> GetSelectedModelsAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(Name, cancellationToken).ConfigureAwait(false);
        if (state?.SelectedModels is { Count: > 0 } stored)
        {
            return stored;
        }
        return _config.Models.Count > 0 ? _config.Models : Array.Empty<string>();
    }

    public async Task<IReadOnlyDictionary<string, ModelInfo>> GetModelInfosAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(Name, cancellationToken).ConfigureAwait(false);
        return state?.ModelInfos ?? new Dictionary<string, ModelInfo>();
    }

    public async Task<string?> GetUpstreamApiBaseUrlAsync(CancellationToken cancellationToken = default)
    {
        var state = await _store.LoadAsync(Name, cancellationToken).ConfigureAwait(false);
        return state?.UpstreamApiBaseUrl is { Length: > 0 } stored ? stored : _config.BaseUrl;
    }

    public async Task PrepareUpstreamRequestAsync(HttpRequestMessage request, CancellationToken cancellationToken = default)
    {
        var token = await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>Parses a "1,3" / "*" model selection against the listed models. Public for testing.</summary>
    public static IReadOnlyList<string> ParseSelection(string input, IReadOnlyList<OpenAiCompatibleModelsClient.ModelEntry> models)
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

    /// <summary>Reads a line without echoing it, falling back to a normal read when input is redirected.</summary>
    private static string ReadSecret()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "";
        }

        var chars = new Stack<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0) chars.Pop();
                continue;
            }
            if (!char.IsControl(key.KeyChar))
            {
                chars.Push(key.KeyChar);
            }
        }

        return new string(chars.Reverse().ToArray());
    }
}
