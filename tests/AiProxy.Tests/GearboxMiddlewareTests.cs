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

public class GearboxMiddlewareTests
{
    private static GearboxState StateFor(GearboxOptions gearbox, string? engage = null)
    {
        var state = new GearboxState(Options.Create(new AiProxyOptions { Gearbox = gearbox }));
        if (engage is not null)
        {
            state.Selected = engage;
        }
        return state;
    }

    private static GearboxMiddleware Middleware(GearboxOptions gearbox, GearboxState state, params IAuthProvider[] providers) =>
        new(Options.Create(new AiProxyOptions { Gearbox = gearbox }), state, providers, NullLogger<GearboxMiddleware>.Instance);

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
        var gearbox = new GearboxOptions
        {
            Enabled = false,
            Gears = { new GearOptions { Position = "1", Model = "opus" } },
        };
        var state = StateFor(gearbox, engage: "1");
        var middleware = Middleware(gearbox, state, new StubProvider("opus"), new StubProvider("sonnet"));

        var context = Context("sonnet", new StubProvider("sonnet"));
        var calls = 0;

        await middleware.InvokeAsync(context, _ => { calls++; return Task.CompletedTask; });

        calls.Should().Be(1);
        context.Model.Should().Be("sonnet");
        context.UpstreamRequest["model"]!.GetValue<string>().Should().Be("sonnet");
    }

    [Fact]
    public async Task Passes_through_in_neutral()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears = { new GearOptions { Position = "1", Model = "opus" } },
        };
        var state = StateFor(gearbox); // defaults to Neutral
        var middleware = Middleware(gearbox, state, new StubProvider("opus"), new StubProvider("sonnet"));

        var context = Context("sonnet", new StubProvider("sonnet"));

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        state.IsNeutral.Should().BeTrue();
        context.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task Shifts_request_onto_engaged_model_and_provider()
    {
        var opus = new StubProvider("opus");
        var sonnet = new StubProvider("sonnet");
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears = { new GearOptions { Position = "4", Label = "Opus", Model = "opus" } },
        };
        var state = StateFor(gearbox, engage: "4");
        var middleware = Middleware(gearbox, state, opus, sonnet);

        var context = Context("sonnet", sonnet);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        context.Model.Should().Be("opus");
        context.Provider.Should().BeSameAs(opus);
        context.UpstreamRequest["model"]!.GetValue<string>().Should().Be("opus");
    }

    [Fact]
    public async Task Passes_through_when_engaged_gear_has_no_model()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears = { new GearOptions { Position = "R", Label = "Default", Model = "" } },
        };
        var state = StateFor(gearbox, engage: "R");
        var middleware = Middleware(gearbox, state, new StubProvider("sonnet"));

        var context = Context("sonnet", new StubProvider("sonnet"));

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        context.Model.Should().Be("sonnet");
    }

    [Fact]
    public async Task Leaves_request_unchanged_when_engaged_model_is_unavailable()
    {
        // Gear points at "haiku", but no connected provider exposes it: fail open.
        var sonnet = new StubProvider("sonnet");
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears = { new GearOptions { Position = "1", Model = "haiku" } },
        };
        var state = StateFor(gearbox, engage: "1");
        var middleware = Middleware(gearbox, state, sonnet);

        var context = Context("sonnet", sonnet);

        await middleware.InvokeAsync(context, _ => Task.CompletedTask);

        context.Model.Should().Be("sonnet");
        context.Provider.Should().BeSameAs(sonnet);
        context.UpstreamRequest["model"]!.GetValue<string>().Should().Be("sonnet");
    }

    [Fact]
    public async Task No_op_when_already_on_engaged_model()
    {
        var opus = new StubProvider("opus");
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears = { new GearOptions { Position = "4", Model = "opus" } },
        };
        var state = StateFor(gearbox, engage: "4");
        var middleware = Middleware(gearbox, state, opus);

        var context = Context("opus", opus);
        var reached = false;

        await middleware.InvokeAsync(context, _ => { reached = true; return Task.CompletedTask; });

        reached.Should().BeTrue();
        context.Model.Should().Be("opus");
        context.Provider.Should().BeSameAs(opus);
    }

    [Fact]
    public void ValidateModels_returns_no_problems_when_disabled()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = false,
            Gears = { new GearOptions { Position = "1", Model = "missing-model" } },
        };
        var middleware = Middleware(gearbox, StateFor(gearbox));

        var problems = middleware.ValidateModels(Array.Empty<ProviderResolver.ProviderModels>());

        problems.Should().BeEmpty();
        gearbox.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ValidateModels_returns_no_problems_when_every_gear_model_is_available()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears =
            {
                new GearOptions { Position = "1", Model = "opus" },
                new GearOptions { Position = "R", Label = "Default", Model = "" },
            },
        };
        var middleware = Middleware(gearbox, StateFor(gearbox));
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(new StubProvider("opus"), new[] { "opus" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        problems.Should().BeEmpty();
        gearbox.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ValidateModels_removes_the_unknown_gear_but_keeps_gearbox_enabled()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears =
            {
                new GearOptions { Position = "1", Label = "Haiku", Model = "gpt-5.6-luna" },
                new GearOptions { Position = "2", Label = "Sonnet", Model = "sonnet" },
            },
        };
        var middleware = Middleware(gearbox, StateFor(gearbox));
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(new StubProvider("sonnet"), new[] { "sonnet" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        gearbox.Enabled.Should().BeTrue();
        gearbox.Gears.Select(g => g.Position).Should().Equal("2");
        var problem = problems.Should().ContainSingle().Which;
        problem.Should().Contain("gpt-5.6-luna");
        problem.Should().Contain("Haiku");
    }

    [Fact]
    public void ValidateModels_disables_gearbox_when_no_gear_with_a_model_survives()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears =
            {
                new GearOptions { Position = "1", Label = "Haiku", Model = "gpt-5.6-luna" },
                new GearOptions { Position = "R", Label = "Default", Model = "" },
            },
        };
        var middleware = Middleware(gearbox, StateFor(gearbox));
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(new StubProvider("sonnet"), new[] { "sonnet" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        gearbox.Enabled.Should().BeFalse();
        problems.Should().ContainSingle().Which.Should().Contain("gpt-5.6-luna");
    }

    [Fact]
    public void ValidateModels_returns_to_neutral_when_the_engaged_gear_is_removed()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Selected = "1",
            Gears =
            {
                new GearOptions { Position = "1", Label = "Haiku", Model = "gpt-5.6-luna" },
                new GearOptions { Position = "2", Label = "Sonnet", Model = "sonnet" },
            },
        };
        var state = StateFor(gearbox);
        var middleware = Middleware(gearbox, state);
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(new StubProvider("sonnet"), new[] { "sonnet" }),
        };

        middleware.ValidateModels(providerModels);

        state.IsNeutral.Should().BeTrue();
    }

    [Fact]
    public void ValidateModels_builds_gears_from_connected_models_when_none_are_configured()
    {
        var gearbox = new GearboxOptions { Enabled = true };
        var middleware = Middleware(gearbox, StateFor(gearbox));
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(
                new StubProvider("opus"), new[] { "opus", "sonnet" }),
            new ProviderResolver.ProviderModels(
                new StubProvider("local"), new[] { "local" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        problems.Should().BeEmpty();
        gearbox.Enabled.Should().BeTrue();
        gearbox.Gears.Select(g => g.Position).Should().Equal("1", "2", "3");
        gearbox.Gears.Select(g => g.Model).Should().Equal("opus", "sonnet", "local");
    }

    [Fact]
    public void ValidateModels_disables_gearbox_when_no_models_are_connected()
    {
        var gearbox = new GearboxOptions { Enabled = true };
        var middleware = Middleware(gearbox, StateFor(gearbox));

        var problems = middleware.ValidateModels(Array.Empty<ProviderResolver.ProviderModels>());

        problems.Should().BeEmpty();
        gearbox.Gears.Should().BeEmpty();
        gearbox.Enabled.Should().BeFalse();
    }

    [Fact]
    public void ValidateModels_caps_auto_gears_at_MaxAutoGears()
    {
        var gearbox = new GearboxOptions { Enabled = true, MaxAutoGears = 2 };
        var middleware = Middleware(gearbox, StateFor(gearbox));
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(
                new StubProvider("opus"), new[] { "opus", "sonnet", "haiku" }),
        };

        middleware.ValidateModels(providerModels);

        gearbox.Gears.Select(g => g.Model).Should().Equal("opus", "sonnet");
    }

    [Fact]
    public void ValidateModels_does_not_build_gears_when_some_are_configured()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears = { new GearOptions { Position = "1", Model = "opus" } },
        };
        var middleware = Middleware(gearbox, StateFor(gearbox));
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(
                new StubProvider("opus"), new[] { "opus", "sonnet" }),
        };

        middleware.ValidateModels(providerModels);

        gearbox.Gears.Select(g => g.Model).Should().Equal("opus");
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
