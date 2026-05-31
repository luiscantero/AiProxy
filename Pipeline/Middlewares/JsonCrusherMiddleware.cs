using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// Lossless JSON-compaction middleware (the SmartCrusher-equivalent). It scans chat message
/// contents for embedded JSON objects/arrays (tool outputs, API responses, DB rows pasted into
/// the prompt) and rewrites each valid span in its minified form before the request is sent to
/// GitHub Copilot.
///
/// The transform is inbound only and purely whitespace/formatting compaction: JSON is parsed and
/// re-serialized compactly with <see cref="System.Text.Json"/>, so no keys, nulls, or values are
/// dropped. It is fail-open — any unexpected error leaves the request untouched.
/// </summary>
public sealed class JsonCrusherMiddleware : IChatMiddleware
{
    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        try
        {
            var savedChars = 0;
            var messageCount = 0;

            if (context.UpstreamRequest["messages"] is JsonArray messages)
            {
                foreach (var msg in messages)
                {
                    if (msg is not JsonObject msgObj)
                        continue;

                    if (msgObj["content"] is JsonValue contentValue)
                    {
                        // String content case: e.g., "content": "user message with embedded JSON".
                        if (contentValue.TryGetValue<string?>(out var contentStr) && contentStr is not null)
                        {
                            var (compacted, saved) = CompactEmbeddedJson(contentStr);
                            if (saved > 0)
                            {
                                msgObj["content"] = compacted;
                                savedChars += saved;
                                messageCount++;
                            }
                        }
                    }
                    else if (msgObj["content"] is JsonArray contentArray)
                    {
                        // Array of content parts case: e.g., "content": [{"type":"text","text":"..."}].
                        foreach (var part in contentArray)
                        {
                            if (part is JsonObject partObj &&
                                partObj["text"] is JsonValue textValue &&
                                textValue.TryGetValue<string?>(out var textStr) &&
                                textStr is not null)
                            {
                                var (compacted, saved) = CompactEmbeddedJson(textStr);
                                if (saved > 0)
                                {
                                    partObj["text"] = compacted;
                                    savedChars += saved;
                                    messageCount++;
                                }
                            }
                        }
                    }
                }
            }

            if (savedChars > 0)
            {
                context.Logger.LogInformation(
                    "JsonCrusher: compacted embedded JSON, saved {SavedChars} chars across {Messages} messages.",
                    savedChars,
                    messageCount);
            }
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(
                ex,
                "JsonCrusher encountered an unexpected exception; proceeding with unchanged request.");
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Scans the input string for embedded JSON objects/arrays, minifies each valid span,
    /// and returns the compacted result and total characters saved.
    /// </summary>
    private static (string compacted, int charsSaved) CompactEmbeddedJson(string content)
    {
        var result = new StringBuilder(content.Length);
        var pos = 0;
        var totalSaved = 0;

        while (pos < content.Length)
        {
            // Find the next potential JSON start ('{' or '[').
            var nextJson = pos;
            while (nextJson < content.Length && content[nextJson] != '{' && content[nextJson] != '[')
            {
                nextJson++;
            }

            if (nextJson >= content.Length)
            {
                // No more JSON starts; append remainder.
                result.Append(content, pos, content.Length - pos);
                break;
            }

            // Append non-JSON text before the next JSON candidate.
            result.Append(content, pos, nextJson - pos);

            // Try to extract and minify the JSON span.
            var (jsonEnd, minified) = TryExtractAndMinifyJson(content, nextJson);

            if (minified is not null)
            {
                var originalLen = jsonEnd - nextJson;
                var minifiedLen = minified.Length;

                if (minifiedLen < originalLen)
                {
                    // Minified form is shorter; use it and count savings.
                    result.Append(minified);
                    totalSaved += originalLen - minifiedLen;
                    pos = jsonEnd;
                }
                else
                {
                    // Minified form is not shorter; keep the original character and advance.
                    result.Append(content[nextJson]);
                    pos = nextJson + 1;
                }
            }
            else
            {
                // Parse failed for this candidate; skip this character and try again.
                result.Append(content[nextJson]);
                pos = nextJson + 1;
            }
        }

        return (result.ToString(), totalSaved);
    }

    /// <summary>
    /// Attempts to find a balanced JSON span starting at startPos by tracking brace/bracket depth
    /// while respecting JSON string literals. If a balanced span is found and parses as valid JSON,
    /// returns the end position and minified form; otherwise returns the end position and null.
    /// </summary>
    private static (int endPos, string? minified) TryExtractAndMinifyJson(string content, int startPos)
    {
        var depth = 0;
        var inString = false;
        var escapeNext = false;
        var openChar = content[startPos];
        var closeChar = openChar == '{' ? '}' : ']';

        for (var i = startPos; i < content.Length; i++)
        {
            var ch = content[i];

            if (escapeNext)
            {
                escapeNext = false;
                continue;
            }

            if (ch == '\\' && inString)
            {
                escapeNext = true;
                continue;
            }

            if (ch == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (ch == openChar)
                {
                    depth++;
                }
                else if (ch == closeChar)
                {
                    depth--;
                    if (depth == 0)
                    {
                        // Balanced span found; attempt to parse and minify.
                        var span = content.Substring(startPos, i - startPos + 1);
                        try
                        {
                            var node = JsonNode.Parse(span);
                            var minified = node?.ToJsonString();
                            return (i + 1, minified);
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            // Parse failed; return end position with null to signal failure.
                            return (i + 1, null);
                        }
                    }
                }
            }
        }

        return (content.Length, null);
    }
}
