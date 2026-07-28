using AiProxy.Storage;

namespace AiProxy.Pipeline;

/// <summary>
/// Publishes a reasoning model at each thinking effort it accepts by exposing one model id per
/// level, Ollama-tag style: <c>gpt-5.6-sol</c> also appears as <c>gpt-5.6-sol:low</c>,
/// <c>gpt-5.6-sol:high</c>, and so on.
///
/// <para>
/// VS Code renders its "thinking effort" control from a <c>configurationSchema</c> that a
/// language-model provider extension attaches to each model. The Ollama provider publishes no
/// such schema, so the control never appears for models reached through this proxy — no matter
/// what <c>/api/tags</c> or <c>/api/show</c> report. Model ids, on the other hand, are fully
/// under our control, so the effort is folded into the id and stripped again on the way
/// upstream, where it becomes the usual <c>reasoning_effort</c> field.
/// </para>
///
/// <para>
/// Which models have levels, and which levels they are, is never configured: both come from the
/// upstream model catalog (<c>capabilities.supports.reasoning_effort</c>). A model that gains,
/// loses or renames a level is followed automatically, and one that has no selectable effort is
/// left alone — sending it a level it does not know would be an upstream 400.
/// </para>
/// </summary>
public static class ReasoningEffort
{
    /// <summary>Separates a model id from its effort suffix. Matches Ollama's <c>name:tag</c> form.</summary>
    public const char Separator = ':';

    /// <summary>
    /// The model ids to publish for <paramref name="model"/>: one per level the upstream says it
    /// accepts, narrowed to <see cref="ReasoningEffortOptions.Levels"/> when that filter is set.
    /// A model with no selectable effort — or none left after filtering — is published as itself,
    /// which sends no <c>reasoning_effort</c> and leaves the choice to the upstream.
    /// </summary>
    public static IReadOnlyList<string> Expand(string model, ModelInfo? info, ReasoningEffortOptions options)
    {
        var levels = LevelsOf(info, options);
        if (options.Levels.Count > 0)
        {
            // An intersection, so a level this model does not offer just drops out.
            levels = levels
                .Where(level => options.Levels.Any(wanted => string.Equals(wanted, level, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        return levels.Count == 0
            ? [model]
            : levels.Select(level => model + Separator + level).ToList();
    }

    /// <summary>
    /// Splits <paramref name="requestedId"/> at its last colon. Purely syntactic: whether the
    /// suffix really is an effort is decided by <see cref="MatchLevel"/>, once the model in front
    /// of it has been resolved.
    /// </summary>
    public static bool TrySplit(string requestedId, out string model, out string suffix)
    {
        model = requestedId;
        suffix = string.Empty;

        var separator = requestedId.LastIndexOf(Separator);
        if (separator <= 0 || separator == requestedId.Length - 1)
        {
            return false;
        }

        model = requestedId[..separator];
        suffix = requestedId[(separator + 1)..];
        return true;
    }

    /// <summary>
    /// The level <paramref name="suffix"/> names, in the upstream's own spelling, or null when the
    /// model does not accept it. A model whose id simply contains a colon (<c>llama3.1:8b</c>)
    /// therefore never has its id truncated. Deliberately not narrowed by
    /// <see cref="ReasoningEffortOptions.Levels"/>: hiding a level from the catalog should not make
    /// it unreachable for a client that names it directly.
    /// </summary>
    public static string? MatchLevel(ModelInfo? info, string suffix, ReasoningEffortOptions options) =>
        LevelsOf(info, options).FirstOrDefault(level => string.Equals(level, suffix, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<string> LevelsOf(ModelInfo? info, ReasoningEffortOptions options) =>
        options.Enabled && info is not null ? info.ReasoningEfforts : Array.Empty<string>();
}
