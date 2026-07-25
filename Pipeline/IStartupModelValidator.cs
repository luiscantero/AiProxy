using AiProxy.Proxy;

namespace AiProxy.Pipeline;

/// <summary>
/// Implemented by middlewares whose configuration references one or more upstream model ids
/// (Caveman, ModelFallback, Gearbox, ...). At startup, once every connected provider has been
/// asked for its model catalog, each registered <see cref="IChatMiddleware"/> that implements
/// this interface is asked to validate its own configuration against that catalog.
///
/// <para>
/// Implementations must be fail-safe: when a configured model cannot be found on any connected
/// provider (typo, renamed/retired model, provider not connected, ...) they should disable
/// themselves - typically by flipping their own <c>Options.Enabled</c> to <c>false</c> - and
/// return one human-readable problem per bad reference so the caller can print a friendly
/// warning. The proxy then continues startup without that middleware rather than failing to
/// start or misbehaving at request time.
/// </para>
/// </summary>
public interface IStartupModelValidator
{
    /// <summary>
    /// Validates this middleware's configured model(s) against the models exposed by all
    /// connected providers. Returns an empty list when configuration is valid (or the feature is
    /// disabled, in which case there is nothing to validate). When problems are found, the
    /// implementation disables its own feature before returning.
    /// </summary>
    IReadOnlyList<string> ValidateModels(IReadOnlyList<ProviderResolver.ProviderModels> providerModels);
}
