using System.Text.Json.Nodes;
using AiProxy;
using AiProxy.Auth;
using AiProxy.Pipeline;
using AiProxy.Pipeline.Middlewares;
using AiProxy.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

public class ModelFallbackMiddlewareTests
{
    private static AiProxyOptions OptionsWith(FallbackOptions fallback) =>
        new() { Fallback = fallback };

    private static ChatPipelineContext Context(string model, IAuthProvider provider) => new()
    {
        Http = new DefaultHttpContext(),
        Surface = ClientSurface.OpenAi,
        Model = model,
        IsStreaming = false,
        UpstreamRequest = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hi" } },
        },
        Provider = provider,
        Logger = NullLogger.Instance,
    };

    [Fact]
    public async Task Passes_through_when_disabled()
    {
        var middleware = new ModelFallbackMiddleware(
            Options.Create(OptionsWith(new FallbackOptions { Enabled = false })),
            Array.Empty<IAuthProvider>());

        var context = Context("primary", new StubProvider("primary"));
        var calls = 0;

        await middleware.InvokeAsync(context, _ => { calls++; return Task.CompletedTask; });

        Assert.Equal(1, calls);
        Assert.Equal("primary", context.Model);
    }

    [Fact]
    public async Task Falls_back_to_next_model_on_retryable_status()
    {
        var providers = new IAuthProvider[] { new StubProvider("primary"), new StubProvider("backup") };
        var fallback = new FallbackOptions
        {
            Enabled = true,
            Chains = { new FallbackChain { Models = { "primary", "backup" } } },
        };
        var middleware = new ModelFallbackMiddleware(Options.Create(OptionsWith(fallback)), providers);

        var context = Context("primary", providers[0]);
        var models = new List<string>();

        await middleware.InvokeAsync(context, ctx =>
        {
            var current = ctx.UpstreamRequest["model"]!.GetValue<string>();
            models.Add(current);
            if (current == "primary")
            {
                throw new UpstreamException(503, "service unavailable");
            }
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "primary", "backup" }, models);
        Assert.Equal("backup", context.Model);
        Assert.Same(providers[1], context.Provider);
        Assert.Equal("backup", context.UpstreamRequest["model"]!.GetValue<string>());
    }

    [Fact]
    public async Task Does_not_fall_back_on_non_retryable_status()
    {
        var providers = new IAuthProvider[] { new StubProvider("primary"), new StubProvider("backup") };
        var fallback = new FallbackOptions
        {
            Enabled = true,
            Chains = { new FallbackChain { Models = { "primary", "backup" } } },
        };
        var middleware = new ModelFallbackMiddleware(Options.Create(OptionsWith(fallback)), providers);

        var context = Context("primary", providers[0]);
        var calls = 0;

        var ex = await Assert.ThrowsAsync<UpstreamException>(() =>
            middleware.InvokeAsync(context, _ =>
            {
                calls++;
                throw new UpstreamException(400, "bad request");
            }));

        Assert.Equal(400, ex.StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Throws_last_error_when_all_candidates_fail()
    {
        var providers = new IAuthProvider[] { new StubProvider("primary"), new StubProvider("backup") };
        var fallback = new FallbackOptions
        {
            Enabled = true,
            Chains = { new FallbackChain { Models = { "primary", "backup" } } },
        };
        var middleware = new ModelFallbackMiddleware(Options.Create(OptionsWith(fallback)), providers);

        var context = Context("primary", providers[0]);

        var ex = await Assert.ThrowsAsync<UpstreamException>(() =>
            middleware.InvokeAsync(context, _ => throw new UpstreamException(429, "rate limited")));

        Assert.Equal(429, ex.StatusCode);
    }

    [Fact]
    public async Task Skips_unresolvable_fallback_models()
    {
        // Only "primary" and "third" are connected; the configured "missing" fallback is skipped.
        var providers = new IAuthProvider[] { new StubProvider("primary"), new StubProvider("third") };
        var fallback = new FallbackOptions
        {
            Enabled = true,
            Chains = { new FallbackChain { Models = { "primary", "missing", "third" } } },
        };
        var middleware = new ModelFallbackMiddleware(Options.Create(OptionsWith(fallback)), providers);

        var context = Context("primary", providers[0]);
        var models = new List<string>();

        await middleware.InvokeAsync(context, ctx =>
        {
            var current = ctx.UpstreamRequest["model"]!.GetValue<string>();
            models.Add(current);
            if (current == "primary")
            {
                throw new UpstreamException(500, "boom");
            }
            return Task.CompletedTask;
        });

        Assert.Equal(new[] { "primary", "third" }, models);
        Assert.Equal("third", context.Model);
    }

    private sealed class StubProvider : IAuthProvider
    {
        private readonly string _model;

        public StubProvider(string model) => _model = model;

        public string Name => _model;

        public Task RunConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RunSelectModelsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> LogoutAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("token");

        public Task<IReadOnlyList<string>> GetSelectedModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(new[] { _model });

        public Task<IReadOnlyDictionary<string, ModelInfo>> GetModelInfosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyDictionary<string, ModelInfo>>(new Dictionary<string, ModelInfo>());

        public Task<string?> GetUpstreamApiBaseUrlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
