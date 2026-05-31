using System.Net.Http;
using AiProxy.Auth;
using AiProxy.Proxy;
using AiProxy.Storage;

namespace AiProxy.Tests;

public class ProviderResolverTests
{
    [Fact]
    public async Task ResolveForModel_finds_owning_provider()
    {
        var a = new StubProvider("a", "gpt-4o");
        var b = new StubProvider("b", "claude-3.5");
        var providers = new IAuthProvider[] { a, b };

        var resolved = await ProviderResolver.ResolveForModelAsync(providers, "claude-3.5", default);

        Assert.Same(b, resolved);
    }

    [Fact]
    public async Task ResolveForModel_returns_null_when_unknown()
    {
        var providers = new IAuthProvider[] { new StubProvider("a", "gpt-4o") };
        Assert.Null(await ProviderResolver.ResolveForModelAsync(providers, "missing", default));
    }

    [Fact]
    public async Task ResolveForModel_first_match_wins_on_collision()
    {
        var a = new StubProvider("a", "shared");
        var b = new StubProvider("b", "shared");

        var resolved = await ProviderResolver.ResolveForModelAsync(new IAuthProvider[] { a, b }, "shared", default);

        Assert.Same(a, resolved);
    }

    [Fact]
    public async Task ListAll_aggregates_and_skips_empty_providers()
    {
        var a = new StubProvider("a", "m1", "m2");
        var empty = new StubProvider("empty");
        var b = new StubProvider("b", "m3");

        var all = await ProviderResolver.ListAllAsync(new IAuthProvider[] { a, empty, b }, default);

        Assert.Equal(2, all.Count);
        Assert.Equal("a", all[0].Provider.Name);
        Assert.Equal(new[] { "m1", "m2" }, all[0].Models);
        Assert.Equal("b", all[1].Provider.Name);
        Assert.Equal(new[] { "m3" }, all[1].Models);
    }

    [Fact]
    public async Task Default_PrepareUpstreamRequest_sets_bearer_from_access_token()
    {
        IAuthProvider provider = new StubProvider("a", "m1") { AccessToken = "tok-123" };

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://example/chat/completions");
        await provider.PrepareUpstreamRequestAsync(request);

        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal("tok-123", request.Headers.Authorization?.Parameter);
    }

    /// <summary>Minimal provider whose model list and token are configurable for routing tests.</summary>
    private sealed class StubProvider : IAuthProvider
    {
        private readonly IReadOnlyList<string> _models;

        public StubProvider(string name, params string[] models)
        {
            Name = name;
            _models = models;
        }

        public string Name { get; }
        public string AccessToken { get; init; } = "token";

        public Task RunConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task RunSelectModelsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> LogoutAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult(AccessToken);
        public Task<IReadOnlyList<string>> GetSelectedModelsAsync(CancellationToken cancellationToken = default) => Task.FromResult(_models);
        public Task<IReadOnlyDictionary<string, ModelInfo>> GetModelInfosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ModelInfo>>(new Dictionary<string, ModelInfo>());
        public Task<string?> GetUpstreamApiBaseUrlAsync(CancellationToken cancellationToken = default) => Task.FromResult<string?>(null);
    }
}
