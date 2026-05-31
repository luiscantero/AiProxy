using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// Log squasher middleware. Server logs pasted into prompts are highly redundant; this inbound-only
/// stage detects log-shaped content in message contents and compresses it by collapsing duplicate
/// lines and thinning long runs of low-severity (TRACE/DEBUG/INFO) lines, while always preserving
/// warnings, errors, and stack traces. It never summarizes semantically and is fail-open.
/// </summary>
public sealed class LogCompressorMiddleware : IChatMiddleware
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex TimestampPattern = new(
        @"^\s*(\d{4}-\d{2}-\d{2}[\sT]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})?|\[\d{2}:\d{2}:\d{2}(?:\.\d+)?\]|\d{2}/\d{2}/\d{4}\s+\d{2}:\d{2}:\d{2})",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex LevelPattern = new(
        @"(?:^|\s)(TRACE|DEBUG|INFO|WARN|WARNING|ERROR|FATAL|CRITICAL)(?:\s|:|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    private static readonly Regex StackTracePattern = new(
        @"(Exception|at\s+\w+\.|\s+at\s+line\s+\d+|Traceback|Caused\s+by:|^\s+at\s+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        try
        {
            ProcessMessages(context);
        }
        catch (RegexMatchTimeoutException ex)
        {
            context.Logger.LogDebug(ex, "LogCompressor: regex timeout during processing; request left unchanged.");
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(ex, "LogCompressor: error during compression; request left unchanged.");
        }

        await next(context).ConfigureAwait(false);
    }

    private static void ProcessMessages(ChatPipelineContext context)
    {
        if (context.UpstreamRequest["messages"] is not JsonArray messages)
            return;

        var totalSavedChars = 0;
        var compressedCount = 0;

        foreach (var msgNode in messages.OfType<JsonObject>())
        {
            // Case 1: content is a string.
            if (msgNode["content"] is JsonValue stringValue)
            {
                if (stringValue.TryGetValue<string?>(out var original) && original is not null)
                {
                    var compressed = CompressLogContent(original);
                    var saved = original.Length - compressed.Length;
                    if (saved > 0)
                    {
                        totalSavedChars += saved;
                        compressedCount++;
                        msgNode["content"] = compressed;
                    }
                }
            }
            // Case 2: content is an array of parts.
            else if (msgNode["content"] is JsonArray partsArray)
            {
                foreach (var part in partsArray.OfType<JsonObject>())
                {
                    if (part["text"] is JsonValue textValue &&
                        textValue.TryGetValue<string?>(out var text) &&
                        text is not null)
                    {
                        var compressed = CompressLogContent(text);
                        var saved = text.Length - compressed.Length;
                        if (saved > 0)
                        {
                            totalSavedChars += saved;
                            compressedCount++;
                            part["text"] = compressed;
                        }
                    }
                }
            }
        }

        if (totalSavedChars > 0)
        {
            context.Logger.LogInformation(
                "LogCompressor: squashed log content, saved {SavedChars} chars across {Blocks} message(s).",
                totalSavedChars,
                compressedCount);
        }
    }

    private static string CompressLogContent(string content)
    {
        var lines = content.Split('\n');

        // Only engage if the content actually looks like a block of logs.
        if (lines.Length < 5)
            return content;

        var logLikeCount = 0;
        foreach (var line in lines)
        {
            if (IsLogLikeLine(line))
                logLikeCount++;
        }

        var logFraction = (double)logLikeCount / lines.Length;
        if (logFraction < 0.4)
            return content;

        var processed = CollapseDuplicates(lines);
        processed = FilterBySeverity(processed);

        var result = string.Join("\n", processed);
        return result.Length < content.Length ? result : content;
    }

    private static bool IsLogLikeLine(string line)
    {
        try
        {
            return TimestampPattern.IsMatch(line) || LevelPattern.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static List<string> CollapseDuplicates(string[] lines)
    {
        var result = new List<string>();
        var i = 0;

        while (i < lines.Length)
        {
            var current = lines[i];
            var currentKey = StripLeadingTimestamp(current);

            var count = 1;
            while (i + count < lines.Length && StripLeadingTimestamp(lines[i + count]) == currentKey)
            {
                count++;
            }

            // Emit the first occurrence verbatim, then a marker for the rest.
            result.Add(current);
            if (count > 1)
                result.Add($"  … (×{count} identical lines)");

            i += count;
        }

        return result;
    }

    private static string StripLeadingTimestamp(string line)
    {
        try
        {
            var match = TimestampPattern.Match(line);
            if (match.Success)
                return line.Substring(match.Length);
        }
        catch (RegexMatchTimeoutException)
        {
        }

        return line;
    }

    private static bool IsHighSeverityLine(string line)
    {
        try
        {
            var levelMatch = LevelPattern.Match(line);
            if (levelMatch.Success)
            {
                var level = levelMatch.Groups[1].Value.ToUpperInvariant();
                if (level is "WARN" or "WARNING" or "ERROR" or "FATAL" or "CRITICAL")
                    return true;
            }

            return StackTracePattern.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            // Be conservative: when in doubt, keep the line.
            return true;
        }
    }

    private static bool IsCollapseMarker(string line) => line.Contains("… (×");

    private static List<string> FilterBySeverity(List<string> lines)
    {
        var result = new List<string>();
        var i = 0;

        while (i < lines.Count)
        {
            // Preserve duplicate-collapse markers.
            if (IsCollapseMarker(lines[i]))
            {
                result.Add(lines[i]);
                i++;
                continue;
            }

            // Always keep high-severity / stack-trace lines.
            if (IsHighSeverityLine(lines[i]))
            {
                result.Add(lines[i]);
                i++;
                continue;
            }

            // Gather a run of consecutive low-severity lines.
            var runStart = i;
            var runLength = 0;
            while (i < lines.Count && !IsCollapseMarker(lines[i]) && !IsHighSeverityLine(lines[i]))
            {
                runLength++;
                i++;
            }

            if (runLength > 10)
            {
                // Keep first 3 and last 2; replace the middle with a marker.
                for (var j = 0; j < 3; j++)
                    result.Add(lines[runStart + j]);

                var omitted = runLength - 5;
                result.Add($"  … (omitted {omitted} low-severity log lines)");

                for (var j = runLength - 2; j < runLength; j++)
                    result.Add(lines[runStart + j]);
            }
            else
            {
                for (var j = 0; j < runLength; j++)
                    result.Add(lines[runStart + j]);
            }
        }

        return result;
    }
}
