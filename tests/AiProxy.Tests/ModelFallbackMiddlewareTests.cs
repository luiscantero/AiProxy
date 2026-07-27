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

    private static ChatPipelineContext Context(string model, IAuthProvider provider, JsonArray? tools = null)
    {
        var request = new JsonObject
        {
            ["model"] = model,
            ["messages"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "hi" } },
        };

        if (tools is not null)
        {
            request["tools"] = tools;
        }

        return new ChatPipelineContext
        {
            Http = new DefaultHttpContext(),
            Surface = ClientSurface.OpenAi,
            Model = model,
            IsStreaming = false,
            UpstreamRequest = request,
            Provider = provider,
            Logger = NullLogger.Instance,
        };
    }

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
    public void ValidateModels_prunes_unknown_models_but_keeps_fallback_enabled()
    {
        var fallback = new FallbackOptions
        {
            Enabled = true,
            Chains = { new FallbackChain { Models = { "primary", "missing-model", "secondary" } } },
        };
        var middleware = Middleware(fallback);
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(
                new StubProvider("primary"), new[] { "primary", "secondary" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        fallback.Enabled.Should().BeTrue();
        fallback.Chains[0].Models.Should().Equal("primary", "secondary");
        problems.Should().ContainSingle().Which.Should().Contain("missing-model");
    }

    [Fact]
    public void ValidateModels_disables_fallback_when_chains_mode_has_nothing_usable()
    {
        var fallback = new FallbackOptions
        {
            Enabled = true,
            Mode = FallbackMode.Chains,
            Chains = { new FallbackChain { Models = { "primary", "missing-model" } } },
        };
        var middleware = Middleware(fallback);
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(new StubProvider("primary"), new[] { "primary" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        fallback.Enabled.Should().BeFalse();
        problems.Should().ContainSingle().Which.Should().Contain("missing-model");
    }

    [Fact]
    public void ValidateModels_keeps_auto_mode_enabled_without_any_chains()
    {
        var fallback = new FallbackOptions { Enabled = true };
        var middleware = Middleware(fallback);

        var problems = middleware.ValidateModels(Array.Empty<ProviderResolver.ProviderModels>());

        problems.Should().BeEmpty();
        fallback.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Auto_falls_back_to_another_connected_model_without_configuration()
    {
        var provider = new StubProvider("primary", "backup");
        var fallback = new FallbackOptions { Enabled = true };
        var middleware = Middleware(fallback, provider);

        var context = Context("primary", provider);
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
    }

    [Fact]
    public async Task Auto_never_falls_back_onto_an_excluded_model()
    {
        var provider = new StubProvider("primary", "backup");
        var fallback = new FallbackOptions { Enabled = true, Exclude = { "backup" } };
        var middleware = Middleware(fallback, provider);

        var context = Context("primary", provider);
        var models = new List<string>();

        Func<Task> act = () => middleware.InvokeAsync(context, ctx =>
        {
            models.Add(ctx.UpstreamRequest["model"]!.GetValue<string>());
            throw new UpstreamException(503, "service unavailable");
        });
        await act.Should().ThrowAsync<UpstreamException>();

        models.Should().Equal("primary");
    }

    [Fact]
    public async Task Auto_skips_candidates_that_cannot_serve_the_request()
    {
        // The request offers tools, so the model that cannot call them is not a valid substitute.
        var infos = new Dictionary<string, ModelInfo>
        {
            ["no-tools"] = new() { SupportsToolCalls = false },
            ["tool-capable"] = new() { SupportsToolCalls = true },
        };
        var provider = new StubProvider(infos, "primary", "no-tools", "tool-capable");
        var fallback = new FallbackOptions { Enabled = true };
        var middleware = Middleware(fallback, provider);

        var tools = new JsonArray { new JsonObject { ["type"] = "function" } };
        var context = Context("primary", provider, tools);
        var models = new List<string>();

        await middleware.InvokeAsync(context, ctx =>
        {
            var current = ctx.UpstreamRequest["model"]!.GetValue<string>();
            models.Add(current);
            if (current == "primary")
            {
                throw new UpstreamException(429, "rate limited");
            }
            return Task.CompletedTask;
        });

        models.Should().Equal("primary", "tool-capable");
    }

    [Fact]
    public async Task Auto_prefers_the_same_family_and_honors_MaxCandidates()
    {
        var infos = new Dictionary<string, ModelInfo>
        {
            ["primary"] = new() { Family = "gpt", MaxContextWindowTokens = 100_000 },
            ["other-family"] = new() { Family = "claude", MaxContextWindowTokens = 200_000 },
            ["same-family"] = new() { Family = "gpt", MaxContextWindowTokens = 128_000 },
        };
        var provider = new StubProvider(infos, "primary", "other-family", "same-family");
        var fallback = new FallbackOptions { Enabled = true, MaxCandidates = 1 };
        var middleware = Middleware(fallback, provider);

        var context = Context("primary", provider);
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

        models.Should().Equal("primary", "same-family");
    }

    [Fact]
    public async Task Auto_skips_candidates_with_a_smaller_context_window()
    {
        var infos = new Dictionary<string, ModelInfo>
        {
            ["primary"] = new() { MaxContextWindowTokens = 200_000 },
            ["too-small"] = new() { MaxContextWindowTokens = 8_000 },
            ["big-enough"] = new() { MaxContextWindowTokens = 200_000 },
        };
        var provider = new StubProvider(infos, "primary", "too-small", "big-enough");
        var fallback = new FallbackOptions { Enabled = true };
        var middleware = Middleware(fallback, provider);

        var context = Context("primary", provider);
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

        models.Should().Equal("primary", "big-enough");
    }

    [Fact]
    public async Task Chains_mode_does_not_fall_back_for_an_unlisted_model()
    {
        var provider = new StubProvider("primary", "backup");
        var fallback = new FallbackOptions { Enabled = true, Mode = FallbackMode.Chains };
        var middleware = Middleware(fallback, provider);

        var context = Context("primary", provider);
        var calls = 0;

        Func<Task> act = () => middleware.InvokeAsync(context, _ =>
        {
            calls++;
            throw new UpstreamException(503, "service unavailable");
        });
        await act.Should().ThrowAsync<UpstreamException>();

        calls.Should().Be(1);
        context.Model.Should().Be("primary");
    }

    private sealed class StubProvider : IAuthProvider
    {
        private readonly IReadOnlyList<string> _models;
        private readonly IReadOnlyDictionary<string, ModelInfo> _infos;

        public StubProvider(params string[] models)
            : this(new Dictionary<string, ModelInfo>(), models)
        {
        }

        public StubProvider(IReadOnlyDictionary<string, ModelInfo> infos, params string[] models)
        {
            _models = models;
            _infos = infos;
        }

        public string Name => _models.Count > 0 ? _models[0] : "stub";

        public Task RunConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task RunSelectModelsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<bool> LogoutAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult("token");

        public Task<IReadOnlyList<string>> GetSelectedModelsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_models);

        public Task<IReadOnlyDictionary<string, ModelInfo>> GetModelInfosAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_infos);

        public Task<string?> GetUpstreamApiBaseUrlAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);
    }
}
