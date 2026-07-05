using Microsoft.Extensions.Options;

namespace AiProxy.Proxy;

/// <summary>
/// Holds the gearbox's runtime state: which gear is currently engaged. This is the single
/// mutable piece of gearbox state shared between the web UI endpoint (which changes the gear)
/// and the <c>GearboxMiddleware</c> (which reads it per request). The available gears themselves
/// are immutable configuration (<see cref="GearboxOptions.Gears"/>); only the selection lives here.
///
/// Registered as a singleton and accessed from concurrent requests, so reads/writes are guarded.
/// </summary>
public sealed class GearboxState
{
    /// <summary>The special position meaning "Neutral" — no model override.</summary>
    public const string Neutral = "N";

    private readonly object _gate = new();
    private string _selected;

    public GearboxState(IOptions<AiProxyOptions> options)
    {
        var selected = options.Value.Gearbox.Selected;
        _selected = string.IsNullOrWhiteSpace(selected) ? Neutral : selected.Trim();
    }

    /// <summary>The currently engaged gear position, or <see cref="Neutral"/> for pass-through.</summary>
    public string Selected
    {
        get { lock (_gate) { return _selected; } }
        set { lock (_gate) { _selected = string.IsNullOrWhiteSpace(value) ? Neutral : value.Trim(); } }
    }

    /// <summary>True when the shifter is in Neutral (no override).</summary>
    public bool IsNeutral =>
        string.Equals(Selected, Neutral, StringComparison.OrdinalIgnoreCase);
}
