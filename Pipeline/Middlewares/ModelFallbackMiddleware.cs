using System.Text.Json.Nodes;
using AiProxy.Auth;
using AiProxy.Proxy;
using AiProxy.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// Resilience middleware: when the model a client requested is unavailable (a provider outage,
/// a rate limit, or any other retryable upstream error), this transparently re-issues the same
/// request against one or more alternative models.
///
/// <para>
/// Alternatives come from one of two places. By default (<see cref="FallbackMode.Auto"/>) they are
/// <b>derived at runtime</b> from the models that are actually connected, ranked by how well they
/// substitute for the failed one — so there are no model ids in configuration to go stale. An
/// explicit <see cref="FallbackChain"/> may still be configured to pin a priority list for a
/// specific model; it wins over Auto for its primary model.
/// </para>
///
/// <para>
/// A fallback model may be hosted by a different provider — it is resolved the same way a
/// directly-requested model is, so the swap also redirects authentication and the upstream base URL.
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
public sealed partial class ModelFallbackMiddleware : IChatMiddleware, IStartupModelValidator, IMiddlewareInfo
{
    private readonly IOptions<AiProxyOptions> _options;
    private readonly IEnumerable<IAuthProvider> _providers;
    private readonly ILogger<ModelFallbackMiddleware> _logger;

    public ModelFallbackMiddleware(
        IOptions<AiProxyOptions> options,
        IEnumerable<IAuthProvider> providers,
        ILogger<ModelFallbackMiddleware> logger)
    {
        _options = options;
        _providers = providers;
        _logger = logger;
    }

    public string Name => "ModelFallback";

    public bool IsEnabled => _options.Value.Fallback.Enabled;

    public string Description
    {
        get
        {
            var fallback = _options.Value.Fallback;
            var source = fallback.Mode == FallbackMode.Auto
                ? $"Alternatives are picked automatically from the connected models (up to " +
                  $"{fallback.MaxCandidates} per request), preferring the same family, a large " +
                  "enough context window, and the capabilities the request actually uses."
                : "Alternatives come only from the configured Fallback.Chains.";

            return "Retries a request against another model when the upstream returns a retryable " +
                   $"status (429, 5xx); the swap is invisible to the client. {source} " +
                   $"{fallback.Chains.Count} explicit chain(s) configured under Fallback.Chains " +
                   "in appsettings.json.";
        }
    }

    /// <summary>
    /// Validates the explicit chains against the models exposed by connected providers. Unknown
    /// models are <b>pruned</b> from their chain rather than switching the whole feature off, so one
    /// retired model id cannot take fallback down with it; the pruned ids are still reported for a
    /// startup warning. Fallback only disables itself when nothing usable is left at all — no chain
    /// with an alternative, and Auto mode switched off.
    /// </summary>
    public IReadOnlyList<string> ValidateModels(IReadOnlyList<ProviderResolver.ProviderModels> providerModels)
    {
        var fallback = _options.Value.Fallback;
        if (!fallback.Enabled)
        {
            return Array.Empty<string>();
        }

        var available = new HashSet<string>(
            providerModels.SelectMany(pm => pm.Models), StringComparer.OrdinalIgnoreCase);

        var problems = new List<string>();
        var usableChains = 0;

        for (var i = 0; i < fallback.Chains.Count; i++)
        {
            var models = fallback.Chains[i].Models;
            var unknown = models.Where(m => !available.Contains(m)).ToList();
            if (unknown.Count > 0)
            {
                // Prune instead of disabling: a chain that still has a primary plus at least one
                // alternative keeps working, and the other chains are unaffected.
                models.RemoveAll(m => !available.Contains(m));
                problems.Add(
                    $"Fallback chain {i + 1}: {string.Join(", ", unknown)} (pruned from the chain)");
            }

            if (models.Count > 1)
            {
                usableChains++;
            }
        }

        if (usableChains == 0 && fallback.Mode != FallbackMode.Auto)
        {
            // Nothing left that could ever fire.
            fallback.Enabled = false;
            return problems;
        }

        LogStartupMode(
            _logger,
            fallback.Mode == FallbackMode.Auto
                ? $"automatic (up to {fallback.MaxCandidates} alternative(s) picked from the connected models)"
                : "explicit chains only",
            usableChains);

        foreach (var chain in fallback.Chains.Where(c => c.Models.Count > 1))
        {
            LogStartupChain(_logger, chain.Models[0], string.Join(", ", chain.Models.Skip(1)));
        }

        return problems;
    }

    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        var options = _options.Value.Fallback;
        if (!options.Enabled)
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        var explicitChain = FindChain(options, context.Model);
        if (explicitChain is null && options.Mode != FallbackMode.Auto)
        {
            // No chain for this model and Auto is off: behave as a pass-through.
            await next(context).ConfigureAwait(false);
            return;
        }

        var primary = context.Model;
        var retryStatusCodes = new HashSet<int>(options.RetryStatusCodes);

        // Alternatives are resolved lazily, on the first failure only, so the happy path never
        // pays for cataloguing the connected models.
        IReadOnlyList<string>? alternatives = null;
        UpstreamException? lastError = null;

        // Why the previous candidate was abandoned; carried into the warning emitted when the
        // next candidate takes over so a single line states both the reason and the new model.
        var failureReason = string.Empty;
        var failedModel = string.Empty;

        for (var attempt = 0; ; attempt++)
        {
            string candidate;

            if (attempt == 0)
            {
                // The model the client asked for, already resolved to its provider.
                candidate = primary;
            }
            else
            {
                alternatives ??= await ResolveAlternativesAsync(options, explicitChain, primary, context)
                    .ConfigureAwait(false);

                if (attempt > alternatives.Count)
                {
                    break;
                }

                candidate = alternatives[attempt - 1];

                // An alternative must be re-resolved to its owning provider, which may differ
                // from the primary's.
                var provider = await ProviderResolver
                    .ResolveForModelAsync(_providers, candidate, context.CancellationToken)
                    .ConfigureAwait(false);

                if (provider is null)
                {
                    LogFallbackModelUnavailable(context.Logger, candidate);
                    continue;
                }

                context.Provider = provider;
                context.Model = candidate;
                context.UpstreamRequest["model"] = candidate;

                LogFallingBack(
                    context.Logger, failedModel, failureReason, candidate, attempt, alternatives.Count);
            }

            try
            {
                await next(context).ConfigureAwait(false);
                if (attempt > 0)
                {
                    LogFallbackServed(context.Logger, candidate);
                }
                return;
            }
            catch (UpstreamException ex) when (retryStatusCodes.Contains(ex.StatusCode))
            {
                lastError = ex;
                failedModel = candidate;
                failureReason = $"upstream returned retryable status {ex.StatusCode}";
                LogRetryableStatus(context.Logger, candidate, ex.StatusCode);
            }
            catch (HttpRequestException ex)
            {
                // A transport-level failure (DNS, connection reset, timeout) is always retryable.
                failedModel = candidate;
                failureReason = $"the request failed at the transport level ({ex.Message})";
                LogTransportFailure(context.Logger, ex, candidate);
            }
        }

        // Every candidate failed (or was unresolvable). Surface the last upstream error so the
        // client sees a real failure rather than an empty success.
        LogFallbackExhausted(context.Logger, primary, failedModel, failureReason);

        if (lastError is not null)
        {
            throw lastError;
        }
    }

    /// <summary>
    /// Returns the alternatives to try after <paramref name="primary"/> failed: the tail of its
    /// explicit chain when one is configured, otherwise an automatically derived list.
    /// </summary>
    private async Task<IReadOnlyList<string>> ResolveAlternativesAsync(
        FallbackOptions options,
        IReadOnlyList<string>? explicitChain,
        string primary,
        ChatPipelineContext context)
    {
        if (explicitChain is not null)
        {
            return explicitChain.Skip(1).ToList();
        }

        return await BuildAutoAlternativesAsync(options, primary, context).ConfigureAwait(false);
    }

    /// <summary>
    /// Derives substitutes for <paramref name="primary"/> from the models that are connected right
    /// now — no configuration required, so the list cannot reference a retired model id.
    ///
    /// <para>
    /// A candidate is only considered when it can actually serve <i>this</i> request: the request
    /// itself states the requirements (image parts need vision, a <c>tools</c> array needs tool
    /// calls), and a model whose context window is known to be smaller than the primary's is
    /// dropped rather than risking a truncation failure. Survivors are ranked by same family
    /// first, then by the closest (smallest sufficient) context window, then by the order the
    /// models were selected in — so failover lands on the nearest equivalent rather than the
    /// biggest or most expensive model available.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<string>> BuildAutoAlternativesAsync(
        FallbackOptions options,
        string primary,
        ChatPipelineContext context)
    {
        var (order, infos) = await CatalogAsync(context.CancellationToken).ConfigureAwait(false);

        infos.TryGetValue(primary, out var primaryInfo);
        var primaryWindow = primaryInfo?.MaxContextWindowTokens;

        var needsVision = RequestHasImages(context.UpstreamRequest);
        var needsToolCalls = RequestHasTools(context.UpstreamRequest);
        var excluded = new HashSet<string>(options.Exclude, StringComparer.OrdinalIgnoreCase);

        var ranked = new List<(string Model, int FamilyRank, long WindowDelta, int SelectionOrder)>();

        for (var i = 0; i < order.Count; i++)
        {
            var candidate = order[i];
            if (string.Equals(candidate, primary, StringComparison.OrdinalIgnoreCase)
                || excluded.Contains(candidate))
            {
                continue;
            }

            infos.TryGetValue(candidate, out var info);

            // A null capability means "the provider didn't say"; assume supported, as the rest of
            // the proxy does.
            if (needsVision && info?.SupportsVision == false)
            {
                continue;
            }

            if (needsToolCalls && info?.SupportsToolCalls == false)
            {
                continue;
            }

            var window = info?.MaxContextWindowTokens;
            if (primaryWindow is int required && window is int actual && actual < required)
            {
                continue;
            }

            var familyRank = primaryInfo?.Family is { Length: > 0 } family
                && string.Equals(info?.Family, family, StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1;

            // An unknown window sorts last within its family group rather than pretending to match.
            var windowDelta = primaryWindow is int p && window is int w ? w - p : long.MaxValue;

            ranked.Add((candidate, familyRank, windowDelta, i));
        }

        ranked.Sort((a, b) =>
        {
            var byFamily = a.FamilyRank.CompareTo(b.FamilyRank);
            if (byFamily != 0)
            {
                return byFamily;
            }

            var byWindow = a.WindowDelta.CompareTo(b.WindowDelta);
            return byWindow != 0 ? byWindow : a.SelectionOrder.CompareTo(b.SelectionOrder);
        });

        var alternatives = ranked
            .Take(Math.Max(0, options.MaxCandidates))
            .Select(r => r.Model)
            .ToList();

        LogAutoAlternatives(
            context.Logger,
            primary,
            alternatives.Count > 0 ? string.Join(", ", alternatives) : "(none)");

        return alternatives;
    }

    /// <summary>
    /// Flattens every connected provider's selected models into one de-duplicated list — in
    /// selection order, which is the user's own priority order — together with their metadata.
    /// </summary>
    private async Task<(List<string> Order, Dictionary<string, ModelInfo> Infos)> CatalogAsync(
        CancellationToken cancellationToken)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var infos = new Dictionary<string, ModelInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var provider in _providers)
        {
            var models = await provider.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models.Count == 0)
            {
                continue;
            }

            var providerInfos = await provider.GetModelInfosAsync(cancellationToken).ConfigureAwait(false);

            foreach (var model in models)
            {
                // First provider wins on id collisions, matching ProviderResolver.
                if (!seen.Add(model))
                {
                    continue;
                }

                order.Add(model);
                if (providerInfos.TryGetValue(model, out var info))
                {
                    infos[model] = info;
                }
            }
        }

        return (order, infos);
    }

    /// <summary>True when any message carries an image content part.</summary>
    private static bool RequestHasImages(JsonObject request)
    {
        if (request["messages"] is not JsonArray messages)
        {
            return false;
        }

        foreach (var message in messages)
        {
            if (message?["content"] is not JsonArray parts)
            {
                continue;
            }

            foreach (var part in parts)
            {
                if (part?["type"]?.GetValue<string>() is "image_url" or "input_image")
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>True when the request offers tools the model would have to be able to call.</summary>
    private static bool RequestHasTools(JsonObject request) =>
        request["tools"] is JsonArray tools && tools.Count > 0;

    /// <summary>
    /// Returns the prioritized model list whose first (requested) entry matches <paramref name="model"/>,
    /// or null when no chain is triggered by it. A chain pruned down to a single model is ignored so
    /// Auto mode can still take over.
    /// </summary>
    private static IReadOnlyList<string>? FindChain(FallbackOptions options, string model)
    {
        foreach (var chain in options.Chains)
        {
            var models = chain.Models;
            if (models.Count > 1 && string.Equals(models[0], model, StringComparison.OrdinalIgnoreCase))
            {
                return models;
            }
        }

        return null;
    }

    // ----------------------------------------------------------------------
    // Structured logging (source-generated)
    // ----------------------------------------------------------------------

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Fallback model {Model} is not exposed by any connected provider; skipping it.")]
    private static partial void LogFallbackModelUnavailable(ILogger logger, string model);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Model {FailedModel} was abandoned because {Reason}; now using {Model} " +
                  "(fallback {Priority} of {Total}).")]
    private static partial void LogFallingBack(
        ILogger logger, string failedModel, string reason, string model, int priority, int total);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Fallback model {Model} served the request.")]
    private static partial void LogFallbackServed(ILogger logger, string model);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Model {Model} returned retryable status {Status}; trying the next fallback.")]
    private static partial void LogRetryableStatus(ILogger logger, string model, int status);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Model {Model} request failed at the transport level; trying the next fallback.")]
    private static partial void LogTransportFailure(ILogger logger, Exception exception, string model);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Fallback for {PrimaryModel} is exhausted; the last candidate {FailedModel} " +
                  "also failed because {Reason}.")]
    private static partial void LogFallbackExhausted(
        ILogger logger, string primaryModel, string failedModel, string reason);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Auto fallback candidates for {Model}: {Alternatives}.")]
    private static partial void LogAutoAlternatives(ILogger logger, string model, string alternatives);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Fallback enabled; alternative selection is {Mode}, with {ChainCount} explicit chain(s).")]
    private static partial void LogStartupMode(ILogger logger, string mode, int chainCount);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Fallback chain for model {Model}; alternatives in priority order: {Alternatives}.")]
    private static partial void LogStartupChain(ILogger logger, string model, string alternatives);
}
