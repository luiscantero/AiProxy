using AiProxy.Auth;
using AiProxy.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// Gearbox ("model shift") middleware: a manual transmission for model routing. Each gear position
/// configured under <see cref="GearboxOptions.Gears"/> is bound to a model; the user shifts between
/// them from the <c>/gearbox</c> web UI, and this middleware re-routes every incoming chat request
/// to whichever model is currently in gear — regardless of the model the client asked for.
///
/// <para>
/// When the shifter is in <b>Neutral</b> (the default), or the engaged gear has no model, the
/// request is passed through untouched so the client's own model choice is honored. When a gear
/// with a model is engaged, the request's model is swapped and the owning provider is re-resolved
/// exactly like a directly-requested model — so a gear can point at a model on any connected
/// provider.
/// </para>
///
/// It runs near the top of the pipeline (just after logging) so the chosen model flows through the
/// remaining transforms. It is <b>fail-open</b>: if the engaged gear maps to a model no connected
/// provider exposes, the request is left on its original model rather than failing.
/// </summary>
public sealed partial class GearboxMiddleware : IChatMiddleware, IStartupModelValidator, IMiddlewareInfo
{
    private readonly IOptions<AiProxyOptions> _options;
    private readonly GearboxState _state;
    private readonly IEnumerable<IAuthProvider> _providers;

    public GearboxMiddleware(IOptions<AiProxyOptions> options, GearboxState state, IEnumerable<IAuthProvider> providers)
    {
        _options = options;
        _state = state;
        _providers = providers;
    }

    public string Name => "Gearbox";

    public bool IsEnabled => _options.Value.Gearbox.Enabled;

    public string Description =>
        "Forces every request onto the model of the gear currently engaged, whatever model the " +
        $"client asked for. Shift gears at {_options.Value.ListenUrl.TrimEnd('/')}/gearbox; " +
        "Neutral (N) passes requests through untouched.";

    /// <summary>
    /// Checks every gear with a configured model against the models exposed by connected
    /// providers. If any gear points at a model nothing exposes, Gearbox is disabled for this
    /// run (fail-safe) and a single problem listing every bad gear is returned for a startup
    /// warning.
    /// </summary>
    public IReadOnlyList<string> ValidateModels(IReadOnlyList<ProviderResolver.ProviderModels> providerModels)
    {
        var gearbox = _options.Value.Gearbox;
        if (!gearbox.Enabled)
        {
            return Array.Empty<string>();
        }

        var available = new HashSet<string>(
            providerModels.SelectMany(pm => pm.Models), StringComparer.OrdinalIgnoreCase);

        var unknown = new List<string>();
        foreach (var gear in gearbox.Gears)
        {
            if (string.IsNullOrWhiteSpace(gear.Model))
            {
                // Pass-through gear; nothing to validate.
                continue;
            }

            if (!available.Contains(gear.Model))
            {
                var label = string.IsNullOrWhiteSpace(gear.Label) ? gear.Position : gear.Label;
                unknown.Add($"'{gear.Position}' ({label}) -> {gear.Model}");
            }
        }

        if (unknown.Count == 0)
        {
            return Array.Empty<string>();
        }

        gearbox.Enabled = false;
        return new[] { $"Gearbox gears: {string.Join(", ", unknown)}" };
    }

    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        var gearbox = _options.Value.Gearbox;
        if (!gearbox.Enabled || _state.IsNeutral)
        {
            // Disabled or in Neutral: honor the client's model choice.
            await next(context).ConfigureAwait(false);
            return;
        }

        var selected = _state.Selected;
        var gear = gearbox.Gears.FirstOrDefault(
            g => string.Equals(g.Position, selected, StringComparison.OrdinalIgnoreCase));

        if (gear is null || string.IsNullOrWhiteSpace(gear.Model))
        {
            // Unknown gear or a pass-through gear (no model): leave the request as-is.
            await next(context).ConfigureAwait(false);
            return;
        }

        if (string.Equals(gear.Model, context.Model, StringComparison.OrdinalIgnoreCase))
        {
            // Already on the engaged model; nothing to swap.
            await next(context).ConfigureAwait(false);
            return;
        }

        var provider = await ProviderResolver
            .ResolveForModelAsync(_providers, gear.Model, context.CancellationToken)
            .ConfigureAwait(false);

        if (provider is null)
        {
            LogGearModelUnavailable(context.Logger, selected, gear.Model, context.Model);
            await next(context).ConfigureAwait(false);
            return;
        }

        LogShifting(context.Logger, selected, context.Model, gear.Model);

        context.Provider = provider;
        context.Model = gear.Model;
        context.UpstreamRequest["model"] = gear.Model;

        await next(context).ConfigureAwait(false);
    }

    // ----------------------------------------------------------------------
    // Structured logging (source-generated)
    // ----------------------------------------------------------------------

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Gearbox gear [{Gear}] maps to model {Model}, which no connected provider exposes; " +
                  "leaving the request on {Original}.")]
    private static partial void LogGearModelUnavailable(ILogger logger, string gear, string model, string original);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Gearbox [{Gear}] shifting request from {Original} to {Model}.")]
    private static partial void LogShifting(ILogger logger, string gear, string original, string model);
}
