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
    private readonly ILogger<GearboxMiddleware> _logger;

    public GearboxMiddleware(
        IOptions<AiProxyOptions> options,
        GearboxState state,
        IEnumerable<IAuthProvider> providers,
        ILogger<GearboxMiddleware> logger)
    {
        _options = options;
        _state = state;
        _providers = providers;
        _logger = logger;
    }

    public string Name => "Gearbox";

    public bool IsEnabled => _options.Value.Gearbox.Enabled;

    public string Description =>
        "Forces every request onto the model of the gear currently engaged, whatever model the " +
        $"client asked for. Shift gears at {_options.Value.ListenUrl.TrimEnd('/')}/gearbox; " +
        "Neutral (N) passes requests through untouched. " +
        (_options.Value.Gearbox.Gears.Count == 0
            ? $"Gears are built automatically from the first {_options.Value.Gearbox.MaxAutoGears} " +
              "connected models."
            : $"{_options.Value.Gearbox.Gears.Count} gear(s) available.");

    /// <summary>
    /// Builds the shifter when no gears were configured, then checks every gear with a model
    /// against the models exposed by connected providers. A gear pointing at a model nothing
    /// exposes is <b>removed from the shifter</b> rather than switching the whole gearbox off, so
    /// one retired model id costs you one gear instead of the feature; the removed gears are still
    /// reported for a startup warning. Gearbox only disables itself when no gear with a model is
    /// left. Finally the gear engaged at startup (and the model it routes to) is logged so the
    /// active selection is visible from the very first line.
    /// </summary>
    public IReadOnlyList<string> ValidateModels(IReadOnlyList<ProviderResolver.ProviderModels> providerModels)
    {
        var gearbox = _options.Value.Gearbox;
        if (!gearbox.Enabled)
        {
            return Array.Empty<string>();
        }

        if (gearbox.Gears.Count == 0)
        {
            // Nothing configured: derive the shifter from what is actually connected. Auto gears
            // are built from the live catalog, so they can never reference a stale model id and
            // the loop below has nothing to prune.
            PopulateAutoGears(gearbox, providerModels);
            LogAutoGears(_logger, gearbox.Gears.Count);
        }

        var available = new HashSet<string>(
            providerModels.SelectMany(pm => pm.Models), StringComparer.OrdinalIgnoreCase);

        var unknown = new List<string>();

        // Walk backwards so removing a gear does not shift the indices still to be checked.
        for (var i = gearbox.Gears.Count - 1; i >= 0; i--)
        {
            var gear = gearbox.Gears[i];
            if (string.IsNullOrWhiteSpace(gear.Model) || available.Contains(gear.Model))
            {
                // Pass-through gear, or one that resolves: nothing to do.
                continue;
            }

            var label = string.IsNullOrWhiteSpace(gear.Label) ? gear.Position : gear.Label;
            unknown.Add($"'{gear.Position}' ({label}) -> {gear.Model}");
            gearbox.Gears.RemoveAt(i);

            if (string.Equals(gear.Position, _state.Selected, StringComparison.OrdinalIgnoreCase))
            {
                // The gear we were about to start in just disappeared; coast in Neutral instead of
                // reporting a position the shifter no longer has.
                _state.Selected = GearboxState.Neutral;
            }
        }

        if (unknown.Count == 0)
        {
            LogStartupSelection(_logger, DescribeSelection(gearbox, _state));
            return Array.Empty<string>();
        }

        // Report in configuration order rather than the reverse order they were removed in.
        unknown.Reverse();
        var problems = new[]
        {
            $"Gearbox gears: {string.Join(", ", unknown)} (removed from the shifter)"
        };

        if (!gearbox.Gears.Any(g => !string.IsNullOrWhiteSpace(g.Model)))
        {
            // Every gear that could route somewhere is gone; there is nothing left to shift into.
            gearbox.Enabled = false;
            return problems;
        }

        LogStartupSelection(_logger, DescribeSelection(gearbox, _state));
        return problems;
    }

    /// <summary>
    /// Fills the shifter with one gear per connected model, in selection order, at positions
    /// "1".."N" (capped by <see cref="GearboxOptions.MaxAutoGears"/>). The label is left empty so
    /// the UI shows the model id itself — there is no invented name to get out of sync.
    /// </summary>
    private static void PopulateAutoGears(
        GearboxOptions gearbox, IReadOnlyList<ProviderResolver.ProviderModels> providerModels)
    {
        var models = providerModels
            .SelectMany(pm => pm.Models)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(Math.Max(0, gearbox.MaxAutoGears));

        var position = 1;
        foreach (var model in models)
        {
            gearbox.Gears.Add(new GearOptions
            {
                Position = position.ToString(),
                Model = model
            });
            position++;
        }
    }

    /// <summary>
    /// Renders the engaged gear as "[position] label -> model", or a Neutral pass-through note.
    /// Shared by the startup log and the gear-change log served by the /gearbox endpoint.
    /// </summary>
    public static string DescribeSelection(GearboxOptions gearbox, GearboxState state)
    {
        if (state.IsNeutral)
        {
            return "Neutral (N) - requests keep the model the client asked for";
        }

        var selected = state.Selected;
        var gear = gearbox.Gears.FirstOrDefault(
            g => string.Equals(g.Position, selected, StringComparison.OrdinalIgnoreCase));

        if (gear is null)
        {
            return $"[{selected}] (unknown gear) - requests keep the model the client asked for";
        }

        var label = string.IsNullOrWhiteSpace(gear.Label) ? gear.Position : gear.Label;
        return string.IsNullOrWhiteSpace(gear.Model)
            ? $"[{gear.Position}] {label} - pass-through, requests keep the model the client asked for"
            : $"[{gear.Position}] {label} -> {gear.Model}";
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

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Gearbox enabled; engaged gear: {Selection}.")]
    private static partial void LogStartupSelection(ILogger logger, string selection);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Gearbox has no configured gears; built {Count} gear(s) from the connected models.")]
    private static partial void LogAutoGears(ILogger logger, int count);
}
