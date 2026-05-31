using System.Net.Http;
using AiProxy.Auth.OpenAiCompatible;
using AiProxy.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

public class OpenAiCompatibleAuthProviderTests
{
    private static OpenAiCompatibleAuthProvider Create(
        InMemoryTokenStore store,
        OpenAiCompatibleProviderOptions config) =>
        new(config, store, new OpenAiCompatibleModelsClient(new HttpClient()), NullLogger.Instance);

    private static OpenAiCompatibleProviderOptions Config(
        string name = "openai",
        string baseUrl = "https://api.openai.com/v1",
        string apiKey = "",
        params string[] models) =>
        new() { Name = name, BaseUrl = baseUrl, ApiKey = apiKey, Models = models.ToList() };

    [Fact]
    public void Constructor_throws_when_name_missing()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Create(new InMemoryTokenStore(), Config(name: "")));
        Assert.Contains("Name", ex.Message);
    }

    [Fact]
    public void Constructor_throws_when_base_url_missing()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            Create(new InMemoryTokenStore(), Config(baseUrl: "")));
        Assert.Contains("BaseUrl", ex.Message);
    }

    [Fact]
    public void Name_comes_from_config()
    {
        var provider = Create(new InMemoryTokenStore(), Config(name: "openrouter"));
        Assert.Equal("openrouter", provider.Name);
    }

    [Fact]
    public async Task GetAccessToken_returns_stored_key()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState { Provider = "openai", ApiKey = "sk-stored" });
        var provider = Create(store, Config(apiKey: "sk-config"));

        Assert.Equal("sk-stored", await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task GetAccessToken_falls_back_to_config_key()
    {
        var provider = Create(new InMemoryTokenStore(), Config(apiKey: "sk-config"));
        Assert.Equal("sk-config", await provider.GetAccessTokenAsync());
    }

    [Fact]
    public async Task GetAccessToken_throws_when_no_key_anywhere()
    {
        var provider = Create(new InMemoryTokenStore(), Config());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetAccessTokenAsync());
        Assert.Contains("connect openai", ex.Message);
    }

    [Fact]
    public async Task GetSelectedModels_returns_stored_over_config()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState
        {
            Provider = "openai",
            SelectedModels = new[] { "gpt-4o" }
        });
        var provider = Create(store, Config(models: new[] { "gpt-3.5" }));

        Assert.Equal(new[] { "gpt-4o" }, await provider.GetSelectedModelsAsync());
    }

    [Fact]
    public async Task GetSelectedModels_falls_back_to_config()
    {
        var provider = Create(new InMemoryTokenStore(), Config(models: new[] { "gpt-4o-mini" }));
        Assert.Equal(new[] { "gpt-4o-mini" }, await provider.GetSelectedModelsAsync());
    }

    [Fact]
    public async Task GetSelectedModels_empty_when_nothing_configured()
    {
        var provider = Create(new InMemoryTokenStore(), Config());
        Assert.Empty(await provider.GetSelectedModelsAsync());
    }

    [Fact]
    public async Task GetUpstreamApiBaseUrl_uses_config_when_no_state()
    {
        var provider = Create(new InMemoryTokenStore(), Config(baseUrl: "https://openrouter.ai/api/v1"));
        Assert.Equal("https://openrouter.ai/api/v1", await provider.GetUpstreamApiBaseUrlAsync());
    }

    [Fact]
    public async Task GetUpstreamApiBaseUrl_uses_stored_when_present()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState { Provider = "openai", UpstreamApiBaseUrl = "https://stored/v1" });
        var provider = Create(store, Config());

        Assert.Equal("https://stored/v1", await provider.GetUpstreamApiBaseUrlAsync());
    }

    [Fact]
    public async Task Logout_returns_false_when_nothing_stored()
    {
        var provider = Create(new InMemoryTokenStore(), Config());
        Assert.False(await provider.LogoutAsync());
    }

    [Fact]
    public async Task Logout_removes_stored_state()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState { Provider = "openai", ApiKey = "sk" });
        var provider = Create(store, Config());

        Assert.True(await provider.LogoutAsync());
        Assert.Null(await store.LoadAsync("openai"));
    }

    [Fact]
    public async Task PrepareUpstreamRequest_sets_bearer_authorization()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState { Provider = "openai", ApiKey = "sk-abc" });
        var provider = Create(store, Config());

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        await provider.PrepareUpstreamRequestAsync(request);

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-abc", request.Headers.Authorization?.Parameter);
    }

    [Fact]
    public void ParseSelection_star_selects_all()
    {
        var models = Models("a", "b", "c");
        Assert.Equal(new[] { "a", "b", "c" }, OpenAiCompatibleAuthProvider.ParseSelection("*", models));
    }

    [Fact]
    public void ParseSelection_indices_map_to_ids_and_dedupe()
    {
        var models = Models("a", "b", "c");
        Assert.Equal(new[] { "a", "c" }, OpenAiCompatibleAuthProvider.ParseSelection("1,3,1", models));
    }

    [Fact]
    public void ParseSelection_rejects_out_of_range()
    {
        var models = Models("a", "b");
        Assert.Throws<FormatException>(() => OpenAiCompatibleAuthProvider.ParseSelection("3", models));
    }

    [Fact]
    public void ParseSelection_rejects_non_numeric()
    {
        var models = Models("a", "b");
        Assert.Throws<FormatException>(() => OpenAiCompatibleAuthProvider.ParseSelection("x", models));
    }

    private static IReadOnlyList<OpenAiCompatibleModelsClient.ModelEntry> Models(params string[] ids) =>
        ids.Select(id => new OpenAiCompatibleModelsClient.ModelEntry(id, null, null)).ToList();
}
