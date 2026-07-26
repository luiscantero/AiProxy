namespace AiProxy.Pipeline;

/// <summary>
/// Self-description for a pipeline stage. Implemented by every <see cref="IChatMiddleware"/> so
/// the startup banner can list which stages are active for this run and tell the user, in one
/// line, what each one does and how to drive it (which options switch it on, which URL to open,
/// ...). Purely informational - it has no effect on request handling.
///
/// <para>
/// <see cref="IsEnabled"/> is read after startup validation has run, so a middleware that
/// disabled itself because of a bad model reference correctly reports <c>false</c>.
/// </para>
/// </summary>
public interface IMiddlewareInfo
{
    /// <summary>Short display name, e.g. "Gearbox".</summary>
    string Name { get; }

    /// <summary>True when this stage actually transforms requests in the current configuration.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// One or two sentences: what the stage does, plus how to use or configure it. Plain text -
    /// the banner word-wraps it.
    /// </summary>
    string Description { get; }
}
