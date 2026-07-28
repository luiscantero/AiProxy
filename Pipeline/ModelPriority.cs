namespace AiProxy.Pipeline;

/// <summary>
/// The single, cross-cutting notion of "which model is stronger" — shared by the gearbox (so gear
/// 1 is the cheapest and the top gear the most powerful) and by model fallback (so a failed model
/// degrades to the nearest step down rather than to whatever happens to be connected).
///
/// <para>
/// The order itself comes from <see cref="AiProxyOptions.ModelPriorityHighToLow"/>, listed most
/// powerful first. Entries are matched <b>tolerantly</b>: an entry may be a full model id
/// (<c>claude-opus-5</c>), the short label the gearbox derives from one (<c>Opus</c>), or any
/// subset of an id's meaningful segments (<c>sonnet-4.5</c>). Short labels are deliberately
/// preferred in configuration because they survive a version bump — <c>Opus</c> still ranks
/// <c>claude-opus-6</c> — which is exactly the staleness the rest of the proxy works to avoid.
/// </para>
///
/// <para>
/// One entry may legitimately match several ids: that defines a <i>tier</i>, not an ambiguity.
/// Where two entries both match a model, the stronger kind of match wins (an exact id beats a
/// label, which beats a segment subset), so listing <c>gpt-4o-mini</c> explicitly still outranks
/// a looser <c>gpt-4o</c> entry above it.
/// </para>
/// </summary>
public static class ModelPriority
{
    /// <summary>Rank returned for a model no entry in the priority list matches.</summary>
    public const int Unranked = int.MaxValue;

    private const int NoMatch = -1;
    private const int ExactMatch = 0;
    private const int LabelMatch = 1;
    private const int SegmentMatch = 2;

    private static readonly char[] LabelSeparators = ['-', '_', ':', '/', ' '];

    /// <summary>
    /// Model ids that carry no information once the rest of the id is on screen: every gear in a
    /// family would repeat them, so they are dropped when something more specific remains.
    /// </summary>
    private static readonly HashSet<string> ModelFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "gpt", "chatgpt", "claude", "gemini", "gemma", "llama", "mistral", "mixtral",
        "qwen", "deepseek", "grok", "phi", "codestral", "command", "nova", "openai"
    };

    /// <summary>
    /// Squeezes a model id into a short label: version-only segments and a leading family name are
    /// dropped, so <c>claude-opus-5</c> becomes "Opus", <c>gemini-3.6-flash</c> becomes "Flash"
    /// and <c>gpt-5.6-luna</c> becomes "Luna". Ids that hold nothing else (<c>gpt-4o</c>,
    /// <c>llama3.1:8b</c>) keep what is left rather than being emptied out, and an id that is
    /// nothing but a version is returned unchanged. Two models can collapse to the same label —
    /// callers that display it (the gearbox readout, the button tooltip) still show the full id,
    /// so the model stays identifiable.
    /// </summary>
    public static string DeriveLabel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return string.Empty;
        }

        var segments = Segments(model);

        if (segments.Count > 1 && ModelFamilies.Contains(segments[0]))
        {
            segments.RemoveAt(0);
        }

        return segments.Count == 0
            ? model
            : string.Join('-', segments.Select(Capitalize));
    }

    /// <summary>
    /// True when a configuration entry names <paramref name="model"/> — as a full id, as its
    /// derived label, or as a subset of its meaningful segments.
    /// </summary>
    public static bool Matches(string entry, string model) => MatchKind(entry, model) != NoMatch;

    /// <summary>
    /// Position of <paramref name="model"/> in the priority list (0 = most powerful), or
    /// <see cref="Unranked"/> when nothing in the list names it. Callers must treat an unranked
    /// model as "no opinion" rather than "worst", so an empty list leaves their own ordering intact.
    /// </summary>
    public static int Rank(IReadOnlyList<string> priority, string model)
    {
        var rank = Unranked;
        var bestKind = int.MaxValue;

        for (var i = 0; i < priority.Count; i++)
        {
            var kind = MatchKind(priority[i], model);

            // A weaker or equal match never displaces an earlier one, so within a match kind the
            // first (strongest) entry that names the model wins.
            if (kind == NoMatch || kind >= bestKind)
            {
                continue;
            }

            bestKind = kind;
            rank = i;
        }

        return rank;
    }

    /// <summary>
    /// Orders <paramref name="models"/> most powerful first. Unranked models keep their incoming
    /// order at the end of the list, so a partial priority list (just your top few) is enough.
    /// </summary>
    public static IEnumerable<string> Order(IReadOnlyList<string> priority, IEnumerable<string> models) =>
        models.OrderBy(m => Rank(priority, m));

    /// <summary>
    /// Removes entries that name none of the <paramref name="available"/> models and returns them
    /// so the caller can raise a startup warning. Pruning rather than failing keeps one retired
    /// model id from costing the whole ordering, matching how the middlewares treat their own
    /// stale model references.
    /// </summary>
    public static IReadOnlyList<string> Prune(List<string> priority, IReadOnlyList<string> available)
    {
        var matched = new HashSet<string>(
            priority.Where(entry => available.Any(model => Matches(entry, model))),
            StringComparer.OrdinalIgnoreCase);

        var unmatched = priority.Where(entry => !matched.Contains(entry)).ToList();
        priority.RemoveAll(entry => !matched.Contains(entry));
        return unmatched;
    }

    /// <summary>
    /// How strongly a configuration entry names a model: <see cref="ExactMatch"/> (the id itself),
    /// <see cref="LabelMatch"/> (the same derived label), <see cref="SegmentMatch"/> (every
    /// meaningful segment of the entry occurs in the id), or <see cref="NoMatch"/>.
    /// </summary>
    private static int MatchKind(string entry, string model)
    {
        if (string.IsNullOrWhiteSpace(entry) || string.IsNullOrWhiteSpace(model))
        {
            return NoMatch;
        }

        if (string.Equals(entry, model, StringComparison.OrdinalIgnoreCase))
        {
            return ExactMatch;
        }

        if (string.Equals(DeriveLabel(entry), DeriveLabel(model), StringComparison.OrdinalIgnoreCase))
        {
            return LabelMatch;
        }

        var entrySegments = Segments(entry);
        if (entrySegments.Count == 0)
        {
            return NoMatch;
        }

        var modelSegments = new HashSet<string>(Segments(model), StringComparer.OrdinalIgnoreCase);
        return entrySegments.All(modelSegments.Contains) ? SegmentMatch : NoMatch;
    }

    /// <summary>Splits a model id into its meaningful (non-version) segments.</summary>
    private static List<string> Segments(string model) => model
        .Split(LabelSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(s => !IsVersion(s))
        .ToList();

    /// <summary>True for segments that are only a version or date stamp: "5", "3.6", "v2", "20240229".</summary>
    private static bool IsVersion(string segment)
    {
        var digits = segment[0] is 'v' or 'V' && segment.Length > 1 ? segment.AsSpan(1) : segment.AsSpan();
        foreach (var c in digits)
        {
            if (!char.IsAsciiDigit(c) && c != '.')
            {
                return false;
            }
        }

        return true;
    }

    private static string Capitalize(string segment) =>
        char.IsLower(segment[0]) ? char.ToUpperInvariant(segment[0]) + segment[1..] : segment;
}
