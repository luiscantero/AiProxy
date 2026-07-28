using AiProxy.Auth;

namespace AiProxy.Proxy;

/// <summary>
/// Routes requests across all registered <see cref="IAuthProvider"/>s by the model id they
/// expose. This is the single place that knows how to pick a provider, so the HTTP endpoints
/// stay provider-agnostic and new providers light up automatically once registered.
/// </summary>
public static class ProviderResolver
{
    /// <summary>A provider paired with the models it currently exposes.</summary>
    public readonly record struct ProviderModels(IAuthProvider Provider, IReadOnlyList<string> Models);

    /// <summary>
    /// Returns the first provider that exposes <paramref name="model"/>, or null if none do.
    /// First-match wins on id collisions across providers.
    /// </summary>
    public static async Task<IAuthProvider?> ResolveForModelAsync(
        IEnumerable<IAuthProvider> providers,
        string model,
        CancellationToken cancellationToken)
    {
        foreach (var provider in providers)
        {
            var models = await provider.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models.Contains(model))
            {
                return provider;
            }
        }
        return null;
    }

    /// <summary>
    /// Returns every provider that currently exposes at least one model, with its model list.
    /// Used to aggregate the model catalog across all providers.
    /// </summary>
    public static async Task<IReadOnlyList<ProviderModels>> ListAllAsync(
        IEnumerable<IAuthProvider> providers,
        CancellationToken cancellationToken)
    {
        var result = new List<ProviderModels>();
        foreach (var provider in providers)
        {
            var models = await provider.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models.Count > 0)
            {
                result.Add(new ProviderModels(provider, models));
            }
        }
        return result;
    }

    /// <summary>A resolved request: the owning provider, the real model id, and the effort asked for.</summary>
    public readonly record struct ResolvedModel(IAuthProvider Provider, string Model, string? ReasoningEffort);

    /// <summary>
    /// Resolves a client-requested model id, which may carry a <c>:&lt;effort&gt;</c> suffix this
    /// proxy publishes on the model's behalf (see <see cref="Pipeline.ReasoningEffort"/>).
    /// Real ids win outright, so a model whose own id contains a colon is never truncated.
    /// Returns null when nothing exposes the model.
    /// </summary>
    public static async Task<ResolvedModel?> ResolveRequestedAsync(
        IEnumerable<IAuthProvider> providers,
        string requestedId,
        ReasoningEffortOptions efforts,
        CancellationToken cancellationToken)
    {
        var exact = await ResolveForModelAsync(providers, requestedId, cancellationToken).ConfigureAwait(false);
        if (exact is not null)
        {
            return new ResolvedModel(exact, requestedId, null);
        }

        if (!efforts.Enabled || !Pipeline.ReasoningEffort.TrySplit(requestedId, out var model, out var suffix))
        {
            return null;
        }

        var provider = await ResolveForModelAsync(providers, model, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return null;
        }

        var infos = await provider.GetModelInfosAsync(cancellationToken).ConfigureAwait(false);
        infos.TryGetValue(model, out var info);

        var level = Pipeline.ReasoningEffort.MatchLevel(info, suffix, efforts);
        return level is null ? null : new ResolvedModel(provider, model, level);
    }
}
