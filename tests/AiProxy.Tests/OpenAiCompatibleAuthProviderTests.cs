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
        Action act = () => Create(new InMemoryTokenStore(), Config(name: ""));
        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("Name");
    }

    [Fact]
    public void Constructor_throws_when_base_url_missing()
    {
        Action act = () => Create(new InMemoryTokenStore(), Config(baseUrl: ""));
        var ex = act.Should().Throw<ArgumentException>().Which;
        ex.Message.Should().Contain("BaseUrl");
    }

    [Fact]
    public void Name_comes_from_config()
    {
        var provider = Create(new InMemoryTokenStore(), Config(name: "openrouter"));
        provider.Name.Should().Be("openrouter");
    }

    [Fact]
    public async Task GetAccessToken_returns_stored_key()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState { Provider = "openai", ApiKey = "sk-stored" });
        var provider = Create(store, Config(apiKey: "sk-config"));

        (await provider.GetAccessTokenAsync()).Should().Be("sk-stored");
    }

    [Fact]
    public async Task GetAccessToken_falls_back_to_config_key()
    {
        var provider = Create(new InMemoryTokenStore(), Config(apiKey: "sk-config"));
        (await provider.GetAccessTokenAsync()).Should().Be("sk-config");
    }

    [Fact]
    public async Task GetAccessToken_throws_when_no_key_anywhere()
    {
        var provider = Create(new InMemoryTokenStore(), Config());
        Func<Task> act = () => provider.GetAccessTokenAsync();
        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().Contain("connect openai");
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

        (await provider.GetSelectedModelsAsync()).Should().Equal("gpt-4o");
    }

    [Fact]
    public async Task GetSelectedModels_falls_back_to_config()
    {
        var provider = Create(new InMemoryTokenStore(), Config(models: new[] { "gpt-4o-mini" }));
        (await provider.GetSelectedModelsAsync()).Should().Equal("gpt-4o-mini");
    }

    [Fact]
    public async Task GetSelectedModels_empty_when_nothing_configured()
    {
        var provider = Create(new InMemoryTokenStore(), Config());
        (await provider.GetSelectedModelsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task GetUpstreamApiBaseUrl_uses_config_when_no_state()
    {
        var provider = Create(new InMemoryTokenStore(), Config(baseUrl: "https://openrouter.ai/api/v1"));
        (await provider.GetUpstreamApiBaseUrlAsync()).Should().Be("https://openrouter.ai/api/v1");
    }

    [Fact]
    public async Task GetUpstreamApiBaseUrl_uses_stored_when_present()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState { Provider = "openai", UpstreamApiBaseUrl = "https://stored/v1" });
        var provider = Create(store, Config());

        (await provider.GetUpstreamApiBaseUrlAsync()).Should().Be("https://stored/v1");
    }

    [Fact]
    public async Task Logout_returns_false_when_nothing_stored()
    {
        var provider = Create(new InMemoryTokenStore(), Config());
        (await provider.LogoutAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Logout_removes_stored_state()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState { Provider = "openai", ApiKey = "sk" });
        var provider = Create(store, Config());

        (await provider.LogoutAsync()).Should().BeTrue();
        (await store.LoadAsync("openai")).Should().BeNull();
    }

    [Fact]
    public async Task PrepareUpstreamRequest_sets_bearer_authorization()
    {
        var store = new InMemoryTokenStore();
        await store.SaveAsync(new AuthState { Provider = "openai", ApiKey = "sk-abc" });
        var provider = Create(store, Config());

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        await provider.PrepareUpstreamRequestAsync(request);

        request.Headers.Authorization?.Scheme.Should().Be("Bearer");
        request.Headers.Authorization?.Parameter.Should().Be("sk-abc");
    }

    [Fact]
    public void ParseSelection_star_selects_all()
    {
        var models = Models("a", "b", "c");
        OpenAiCompatibleAuthProvider.ParseSelection("*", models).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void ParseSelection_indices_map_to_ids_and_dedupe()
    {
        var models = Models("a", "b", "c");
        OpenAiCompatibleAuthProvider.ParseSelection("1,3,1", models).Should().Equal("a", "c");
    }

    [Fact]
    public void ParseSelection_rejects_out_of_range()
    {
        var models = Models("a", "b");
        Action act = () => OpenAiCompatibleAuthProvider.ParseSelection("3", models);
        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void ParseSelection_rejects_non_numeric()
    {
        var models = Models("a", "b");
        Action act = () => OpenAiCompatibleAuthProvider.ParseSelection("x", models);
        act.Should().Throw<FormatException>();
    }

    private static IReadOnlyList<OpenAiCompatibleModelsClient.ModelEntry> Models(params string[] ids) =>
        ids.Select(id => new OpenAiCompatibleModelsClient.ModelEntry(id, null, null)).ToList();
}
