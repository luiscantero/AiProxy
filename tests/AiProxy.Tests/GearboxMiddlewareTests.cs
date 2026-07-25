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
        new(Options.Create(new AiProxyOptions { Gearbox = gearbox }), state, providers);

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

        Assert.Equal(1, calls);
        Assert.Equal("sonnet", context.Model);
        Assert.Equal("sonnet", context.UpstreamRequest["model"]!.GetValue<string>());
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

        Assert.True(state.IsNeutral);
        Assert.Equal("sonnet", context.Model);
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

        Assert.Equal("opus", context.Model);
        Assert.Same(opus, context.Provider);
        Assert.Equal("opus", context.UpstreamRequest["model"]!.GetValue<string>());
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

        Assert.Equal("sonnet", context.Model);
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

        Assert.Equal("sonnet", context.Model);
        Assert.Same(sonnet, context.Provider);
        Assert.Equal("sonnet", context.UpstreamRequest["model"]!.GetValue<string>());
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

        Assert.True(reached);
        Assert.Equal("opus", context.Model);
        Assert.Same(opus, context.Provider);
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

        Assert.Empty(problems);
        Assert.False(gearbox.Enabled);
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

        Assert.Empty(problems);
        Assert.True(gearbox.Enabled);
    }

    [Fact]
    public void ValidateModels_disables_gearbox_and_reports_problem_for_unknown_model()
    {
        var gearbox = new GearboxOptions
        {
            Enabled = true,
            Gears = { new GearOptions { Position = "1", Label = "Haiku", Model = "gpt-5.6-luna" } },
        };
        var middleware = Middleware(gearbox, StateFor(gearbox));
        var providerModels = new[]
        {
            new ProviderResolver.ProviderModels(new StubProvider("sonnet"), new[] { "sonnet" }),
        };

        var problems = middleware.ValidateModels(providerModels);

        Assert.False(gearbox.Enabled);
        var problem = Assert.Single(problems);
        Assert.Contains("gpt-5.6-luna", problem);
        Assert.Contains("Haiku", problem);
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
