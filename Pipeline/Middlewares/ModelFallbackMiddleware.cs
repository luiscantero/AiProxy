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

    public string Description =>
        "Retries a request against the next model in its chain when the upstream returns a " +
        $"retryable status (429, 5xx); the swap is invisible to the client. " +
        $"{_options.Value.Fallback.Chains.Count} chain(s) configured under Fallback.Chains " +
        "in appsettings.json.";

    /// <summary>
    /// Checks every model referenced by every configured chain against the models exposed by
    /// connected providers. If any chain references a model nothing exposes, Fallback is
    /// disabled for this run (fail-safe) and one problem per affected chain (listing all of its
    /// unknown models) is returned for a startup warning. When every chain is valid, each chain's
    /// primary model (the one clients request) and its alternatives are logged.
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
        for (var i = 0; i < fallback.Chains.Count; i++)
        {
            var unknown = fallback.Chains[i].Models.Where(m => !available.Contains(m)).ToList();
            if (unknown.Count > 0)
            {
                problems.Add($"Fallback chain {i + 1}: {string.Join(", ", unknown)}");
            }
        }

        if (problems.Count > 0)
        {
            fallback.Enabled = false;
            return problems;
        }

        foreach (var chain in fallback.Chains)
        {
            if (chain.Models.Count == 0)
            {
                continue;
            }

            LogStartupChain(
                _logger,
                chain.Models[0],
                chain.Models.Count > 1 ? string.Join(", ", chain.Models.Skip(1)) : "(none)");
        }

        return problems;
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

        // Why the previous candidate was abandoned; carried into the warning emitted when the
        // next candidate takes over so a single line states both the reason and the new model.
        var failureReason = string.Empty;
        var failedModel = string.Empty;

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
                    LogFallbackModelUnavailable(context.Logger, candidate);
                    continue;
                }

                context.Provider = provider;
                context.Model = candidate;
                context.UpstreamRequest["model"] = candidate;

                LogFallingBack(context.Logger, failedModel, failureReason, candidate, i + 1, chain.Count);
            }

            attempted = true;

            try
            {
                await next(context).ConfigureAwait(false);
                if (i > 0)
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
        if (failedModel.Length > 0)
        {
            LogChainExhausted(context.Logger, chain[0], failedModel, failureReason);
        }

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
                  "(fallback priority {Priority} of {Total}).")]
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
        Message = "Fallback chain for {PrimaryModel} is exhausted; the last candidate {FailedModel} " +
                  "also failed because {Reason}.")]
    private static partial void LogChainExhausted(
        ILogger logger, string primaryModel, string failedModel, string reason);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Fallback enabled for model {Model}; alternatives in priority order: {Alternatives}.")]
    private static partial void LogStartupChain(ILogger logger, string model, string alternatives);
}
