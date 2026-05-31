using System.Text.Json;

namespace AiProxy.Pipeline;

/// <summary>
/// A single normalized chunk of an upstream chat response.
///
/// For streaming responses one chunk maps to one upstream SSE <c>data:</c> frame.
/// For non-streaming responses the whole response is represented as a single chunk.
///
/// The chunk is deliberately wire-format-agnostic: surface adapters translate it into
/// OpenAI SSE or Ollama NDJSON, and middlewares can rewrite <see cref="ContentDelta"/>
/// (e.g. to decompress text) without caring about the downstream format.
/// </summary>
public sealed class ChatResponseChunk
{
    /// <summary>Incremental assistant text for this chunk, if any.</summary>
    public string? ContentDelta { get; set; }

    /// <summary>Raw tool-call fragments for this chunk, passed through verbatim.</summary>
    public IReadOnlyList<JsonElement>? ToolCalls { get; set; }

    /// <summary>The finish reason once the upstream signals completion (e.g. "stop").</summary>
    public string? FinishReason { get; set; }

    /// <summary>Prompt token count reported by the upstream usage block, if present.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Completion token count reported by the upstream usage block, if present.</summary>
    public int? CompletionTokens { get; set; }
}
