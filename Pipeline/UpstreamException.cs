namespace AiProxy.Pipeline;

/// <summary>
/// Thrown by the terminal invoker when the upstream (GitHub Copilot) returns a non-success
/// status. Surface adapters catch this and translate it into their own error wire format.
/// </summary>
public sealed class UpstreamException : Exception
{
    public UpstreamException(int statusCode, string body)
        : base($"Upstream returned {statusCode}.")
    {
        StatusCode = statusCode;
        Body = body;
    }

    public int StatusCode { get; }

    public string Body { get; }
}
