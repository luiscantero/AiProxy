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
public sealed class GearboxMiddleware : IChatMiddleware
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
            context.Logger.LogWarning(
                "Gearbox gear [{Gear}] maps to model {Model}, which no connected provider exposes; " +
                "leaving the request on {Original}.",
                selected, gear.Model, context.Model);
            await next(context).ConfigureAwait(false);
            return;
        }

        context.Logger.LogInformation(
            "Gearbox [{Gear}] shifting request from {Original} to {Model}.",
            selected, context.Model, gear.Model);

        context.Provider = provider;
        context.Model = gear.Model;
        context.UpstreamRequest["model"] = gear.Model;

        await next(context).ConfigureAwait(false);
    }
}
