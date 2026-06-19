using System.Text.Json.Nodes;
using AiProxy.Auth;
using AiProxy.Proxy;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// Resilience middleware: when the model a client requested is unavailable (a provider outage,
/// a rate limit, or any other retryable upstream error), this transparently re-issues the same
/// request against a prioritized list of alternative models.
///
/// <para>
/// Each configured <see cref="FallbackChain"/> lists models in priority order. The first entry is
/// the model clients request; the remaining entries are tried, in order, when an attempt fails
/// with a status in <see cref="FallbackOptions.RetryStatusCodes"/>. A fallback model may be hosted
/// by a different provider — it is resolved the same way a directly-requested model is, so the swap
/// also redirects authentication and the upstream base URL.
/// </para>
///
/// <para>
/// The upstream call fails fast (the terminal invoker validates the response status before exposing
/// any chunks), so a fallback happens before a single byte has been streamed to the client — the
/// retry is invisible to the caller. Non-retryable errors (e.g. a 400 for a malformed request) are
/// propagated unchanged so genuine client mistakes are not masked.
/// </para>
///
/// This middleware is registered innermost (closest to the terminal upstream invoker) so the
/// outer prompt-transform middlewares run only once; each fallback attempt simply re-sends the
/// already-transformed request with a different model id.
/// </summary>
public sealed class ModelFallbackMiddleware : IChatMiddleware
{
    private readonly IOptions<AiProxyOptions> _options;
    private readonly IEnumerable<IAuthProvider> _providers;

    public ModelFallbackMiddleware(IOptions<AiProxyOptions> options, IEnumerable<IAuthProvider> providers)
    {
        _options = options;
        _providers = providers;
    }

    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        var options = _options.Value.Fallback;

        var chain = options.Enabled ? FindChain(options, context.Model) : null;
        if (chain is null || chain.Count <= 1)
        {
            // No fallback configured for this model: behave as a pass-through.
            await next(context).ConfigureAwait(false);
            return;
        }

        var retryStatusCodes = new HashSet<int>(options.RetryStatusCodes);
        UpstreamException? lastError = null;
        var attempted = false;

        for (var i = 0; i < chain.Count; i++)
        {
            var candidate = chain[i];

            // The first entry is the already-resolved primary model; subsequent entries must be
            // re-resolved to their owning provider (which may differ from the primary's).
            if (i > 0)
            {
                var provider = await ProviderResolver
                    .ResolveForModelAsync(_providers, candidate, context.CancellationToken)
                    .ConfigureAwait(false);

                if (provider is null)
                {
                    context.Logger.LogWarning(
                        "Fallback model {Model} is not exposed by any connected provider; skipping it.",
                        candidate);
                    continue;
                }

                context.Provider = provider;
                context.Model = candidate;
                context.UpstreamRequest["model"] = candidate;

                context.Logger.LogWarning(
                    "Falling back to model {Model} (priority {Priority} of {Total}).",
                    candidate, i + 1, chain.Count);
            }

            attempted = true;

            try
            {
                await next(context).ConfigureAwait(false);
                if (i > 0)
                {
                    context.Logger.LogInformation("Fallback model {Model} served the request.", candidate);
                }
                return;
            }
            catch (UpstreamException ex) when (retryStatusCodes.Contains(ex.StatusCode))
            {
                lastError = ex;
                context.Logger.LogWarning(
                    "Model {Model} returned retryable status {Status}; trying the next fallback.",
                    candidate, ex.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                // A transport-level failure (DNS, connection reset, timeout) is always retryable.
                context.Logger.LogWarning(
                    ex, "Model {Model} request failed at the transport level; trying the next fallback.",
                    candidate);
            }
        }

        // Every candidate failed (or was unresolvable). Surface the last upstream error so the
        // client sees a real failure rather than an empty success.
        if (lastError is not null)
        {
            throw lastError;
        }

        if (!attempted)
        {
            // No candidate was resolvable: run the pipeline once with the original request so the
            // upstream returns its own (authoritative) error.
            await next(context).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Returns the prioritized model list whose first (requested) entry matches <paramref name="model"/>,
    /// or null when no chain is triggered by it.
    /// </summary>
    private static IReadOnlyList<string>? FindChain(FallbackOptions options, string model)
    {
        foreach (var chain in options.Chains)
        {
            var models = chain.Models;
            if (models.Count > 0 && string.Equals(models[0], model, StringComparison.OrdinalIgnoreCase))
            {
                return models;
            }
        }

        return null;
    }
}
