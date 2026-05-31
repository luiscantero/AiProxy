using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// CacheAligner middleware. Provider prompt caches key off a stable prefix, so volatile tokens
/// (today's date, UUIDs, session/request ids, timestamps, epoch seconds) embedded in the SYSTEM
/// prompt force a cache miss on every request and inflate cost. This middleware rewrites those
/// volatile tokens to fixed placeholders so the cacheable prefix stays byte-stable across requests.
///
/// It operates inbound only and only on system messages — user/assistant/tool content is left
/// untouched so real conversation data is never corrupted. It is fail-open.
/// </summary>
public sealed class CacheAlignerMiddleware : IChatMiddleware
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    // RFC 4122 UUID: 8-4-4-4-12 hex digits.
    private static readonly Regex UuidPattern = new(
        @"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase,
        RegexTimeout);

    // ISO-8601 datetime: 2026-05-31T07:00:00Z, with optional fractional seconds and offset.
    private static readonly Regex IsoDateTimePattern = new(
        @"\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})?",
        RegexOptions.Compiled,
        RegexTimeout);

    // Plain date YYYY-MM-DD.
    private static readonly Regex PlainDatePattern = new(
        @"\b\d{4}-\d{2}-\d{2}\b",
        RegexOptions.Compiled,
        RegexTimeout);

    // Unix epoch seconds (10 digits) or millis (13 digits), bounded so other numbers are left alone.
    private static readonly Regex EpochPattern = new(
        @"\b\d{10}(?:\d{3})?\b",
        RegexOptions.Compiled,
        RegexTimeout);

    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        try
        {
            StabilizeSystemPrompt(context);
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(ex, "CacheAligner encountered an error; request left unchanged.");
        }

        await next(context).ConfigureAwait(false);
    }

    private static void StabilizeSystemPrompt(ChatPipelineContext context)
    {
        if (context.UpstreamRequest["messages"] is not JsonArray messages)
            return;

        var totalReplacements = 0;

        foreach (var msg in messages)
        {
            if (msg is not JsonObject msgObj)
                continue;

            var role = msgObj["role"]?.GetValue<string>();
            if (role is null || !role.Equals("system", StringComparison.OrdinalIgnoreCase))
                continue;

            totalReplacements += ProcessMessageContent(msgObj);
        }

        if (totalReplacements > 0)
        {
            context.Logger.LogInformation(
                "CacheAligner: stabilized {Count} volatile token(s) in system prompt for cache reuse.",
                totalReplacements);
        }
    }

    private static int ProcessMessageContent(JsonObject msgObj)
    {
        var replacements = 0;

        // Case 1: content is a string.
        if (msgObj["content"] is JsonValue contentValue)
        {
            if (contentValue.TryGetValue<string?>(out var text) && !string.IsNullOrEmpty(text))
            {
                var stabilized = StabilizeText(text, ref replacements);
                if (!ReferenceEquals(stabilized, text) && stabilized != text)
                {
                    msgObj["content"] = stabilized;
                }
            }
        }
        // Case 2: content is an array of content parts.
        else if (msgObj["content"] is JsonArray contentArray)
        {
            foreach (var part in contentArray)
            {
                if (part is JsonObject partObj &&
                    partObj["text"] is JsonValue textValue &&
                    textValue.TryGetValue<string?>(out var text) &&
                    !string.IsNullOrEmpty(text))
                {
                    var stabilized = StabilizeText(text, ref replacements);
                    if (stabilized != text)
                    {
                        partObj["text"] = stabilized;
                    }
                }
            }
        }

        return replacements;
    }

    private static string StabilizeText(string text, ref int replacementCount)
    {
        var result = text;

        // ISO-8601 datetimes first (most specific, so the plain-date pass does not split them).
        result = ReplaceWithCount(IsoDateTimePattern, result, "<TIMESTAMP>", ref replacementCount);
        result = ReplaceWithCount(PlainDatePattern, result, "<DATE>", ref replacementCount);
        result = ReplaceWithCount(UuidPattern, result, "<UUID>", ref replacementCount);
        result = ReplaceWithCount(EpochPattern, result, "<EPOCH>", ref replacementCount);

        return result;
    }

    private static string ReplaceWithCount(Regex pattern, string input, string replacement, ref int count)
    {
        try
        {
            var matches = pattern.Matches(input).Count;
            if (matches == 0)
                return input;

            count += matches;
            return pattern.Replace(input, replacement);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail-open: skip this pattern, leave the text unchanged.
            return input;
        }
    }
}
