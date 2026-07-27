using System.Text.Json.Nodes;
using AiProxy;
using AiProxy.Auth;
using AiProxy.Pipeline;
using AiProxy.Pipeline.Middlewares;
using AiProxy.Proxy;
using AiProxy.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

public class ModelFallbackMiddlewareTests
{
    private static AiProxyOptions OptionsWith(FallbackOptions fallback) =>
        new() { Fallback = fallback };

    private static ModelFallbackMiddleware Middleware(FallbackOptions fallback, params IAuthProvider[] providers) =>
        new(Options.Create(OptionsWith(fallback)), providers, NullLogger<ModelFallbackMiddleware>.Instance);

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
        var middleware = Middleware(new FallbackOptions { Enabled = false });

        var context = Context("primary", new StubProvider("primary"));
        var calls = 0;

        await middleware.InvokeAsync(context, _ => { calls++; return Task.CompletedTask; });

        calls.Should().Be(1);
        context.Model.Should().Be("primary");
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
        var middleware = Middleware(fallback, providers);

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

        models.Should().Equal("primary", "backup");
        context.Model.Should().Be("backup");
        context.Provider.Should().BeSameAs(providers[1]);
        context.UpstreamRequest["model"]!.GetValue<string>().Should().Be("backup");
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
        var middleware = Middleware(fallback, providers);

        var context = Context("primary", providers[0]);
        var calls = 0;

        Func<Task> act = () => middleware.InvokeAsync(context, _ =>
        {
            calls++;
            throw new UpstreamException(400, "bad request");
        });
        var ex = (await act.Should().ThrowAsync<UpstreamException>()).Which;

        ex.StatusCode.Should().Be(400);
        calls.Should().Be(1);
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
        var middleware = Middleware(fallback, providers);

        var context = Context("primary", providers[0]);

        Func<Task> act = () =>
            middleware.InvokeAsync(context, _ => throw new UpstreamException(429, "rate limited"));
        var ex = (await act.Should().ThrowAsync<UpstreamException>()).Which;

        ex.StatusCode.Should().Be(429);
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
        var middleware = Middleware(fallback, providers);

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

        models.Should().Equal("primary", "third");
        context.Model.Should().Be("third");
    }

    [Fact]
    public void ValidateModels_returns_no_problems_when_disabled()
    {
        var fallback = new FallbackOptions
        {
            Enabled = false,
            Chains = { new FallbackChain { Models = { "primary", "missing" } } },
        };
        var middleware = Middleware(fallback);

        var problems = middleware.ValidateModels(Array.Empty<ProviderResolver.ProviderModels>());

        problems.Should().BeEmpty();
        fallback.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ValidateModels_returns_no_problems_when_every_chain_model_is_available()
    {
        var fallback = new FallbackOptions
        {
            Enabled = true,
            Chains = { new FallbackChain { Models = { "primary", "secondary" } } },
        };
        var middleware = Middleware(fallback);
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(
                new StubProvider("primary"), new[] { "primary", "secondary" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        problems.Should().BeEmpty();
        fallback.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ValidateModels_disables_fallback_and_reports_problem_for_unknown_model()
    {
        var fallback = new FallbackOptions
        {
            Enabled = true,
            Chains = { new FallbackChain { Models = { "primary", "missing-model" } } },
        };
        var middleware = Middleware(fallback);
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(new StubProvider("primary"), new[] { "primary" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        fallback.Enabled.Should().BeFalse();
        var problem = problems.Should().ContainSingle().Which;
        problem.Should().Contain("missing-model");
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
