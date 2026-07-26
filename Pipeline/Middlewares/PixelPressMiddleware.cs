using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>
/// PixelPress middleware (a pxpipe-style transform). Bulky prompt text — typically the system
/// prompt and tool documentation — is rasterized into a dense monospace PNG and swapped into the
/// message as an <c>image_url</c> content part. A vision-capable upstream model then pays a fixed
/// image-token cost to read the pixels instead of a per-character text-token cost, which can cut
/// tokens sharply on large, mostly-static blocks.
///
/// The transform is inbound-only and <b>lossy</b>: the model OCRs the rendered pixels, so exact
/// strings (hashes, long ids) can be misread and a vision model is required. For those reasons it
/// is disabled by default and enabled via <see cref="PixelPressOptions.Enabled"/>. It is fail-open:
/// any rendering error leaves the request untouched.
/// </summary>
public sealed class PixelPressMiddleware : IChatMiddleware, IMiddlewareInfo
{
    private const string HintText =
        "The following image contains text rendered as pixels to save tokens. " +
        "Read every character in the image exactly as if it were part of this message.";

    private readonly IOptions<AiProxyOptions> _options;

    public PixelPressMiddleware(IOptions<AiProxyOptions> options)
    {
        _options = options;
    }

    public string Name => "PixelPress";

    public bool IsEnabled => _options.Value.PixelPress.Enabled;

    public string Description =>
        $"Renders {string.Join("/", _options.Value.PixelPress.Roles.Distinct(StringComparer.OrdinalIgnoreCase))} " +
        $"text longer than {_options.Value.PixelPress.MinCharacters} characters into a PNG the " +
        "model reads instead. Lossy - requires a vision-capable model; tune via PixelPress.* " +
        "in appsettings.json.";

    public async Task InvokeAsync(ChatPipelineContext context, ChatMiddlewareDelegate next)
    {
        var cfg = _options.Value.PixelPress;

        if (cfg.Enabled)
        {
            try
            {
                RenderMessages(context, cfg);
            }
            catch (Exception ex)
            {
                context.Logger.LogDebug(ex, "PixelPress: rendering failed; request left unchanged.");
            }
        }

        await next(context).ConfigureAwait(false);
    }

    private static void RenderMessages(ChatPipelineContext context, PixelPressOptions cfg)
    {
        if (context.UpstreamRequest["messages"] is not JsonArray messages)
        {
            return;
        }

        var roles = new HashSet<string>(cfg.Roles, StringComparer.OrdinalIgnoreCase);
        var renderedBlocks = 0;
        var renderedChars = 0;

        foreach (var msg in messages.OfType<JsonObject>())
        {
            var role = msg["role"]?.GetValue<string>();
            if (role is null || !roles.Contains(role))
            {
                continue;
            }

            // Case 1: content is a plain string.
            if (msg["content"] is JsonValue contentValue
                && contentValue.TryGetValue<string?>(out var text)
                && text is not null)
            {
                if (text.Length < cfg.MinCharacters)
                {
                    continue;
                }

                var parts = new JsonArray();
                if (cfg.IncludeHint)
                {
                    parts.Add(TextPart(HintText));
                }
                parts.Add(ImagePart(RenderToDataUrl(text, cfg)));
                msg["content"] = parts;

                renderedBlocks++;
                renderedChars += text.Length;
            }
            // Case 2: content is an array of parts; render each long text part in place.
            else if (msg["content"] is JsonArray existingParts)
            {
                var newParts = new JsonArray();
                var replacedInThisMessage = false;

                foreach (var part in existingParts)
                {
                    if (part is JsonObject partObj
                        && partObj["type"]?.GetValue<string>() == "text"
                        && partObj["text"] is JsonValue partText
                        && partText.TryGetValue<string?>(out var partStr)
                        && partStr is not null
                        && partStr.Length >= cfg.MinCharacters)
                    {
                        newParts.Add(ImagePart(RenderToDataUrl(partStr, cfg)));
                        replacedInThisMessage = true;
                        renderedBlocks++;
                        renderedChars += partStr.Length;
                    }
                    else
                    {
                        // Detach the node from its current parent before re-adding it.
                        newParts.Add(part?.DeepClone());
                    }
                }

                if (!replacedInThisMessage)
                {
                    continue;
                }

                if (cfg.IncludeHint)
                {
                    newParts.Insert(0, TextPart(HintText));
                }

                msg["content"] = newParts;
            }
        }

        if (renderedBlocks > 0)
        {
            context.Logger.LogInformation(
                "PixelPress: rendered {Blocks} text block(s) ({Chars} chars) to PNG before upstream.",
                renderedBlocks, renderedChars);
        }
    }

    private static JsonObject TextPart(string text) => new()
    {
        ["type"] = "text",
        ["text"] = text,
    };

    private static JsonObject ImagePart(string dataUrl) => new()
    {
        ["type"] = "image_url",
        ["image_url"] = new JsonObject { ["url"] = dataUrl },
    };

    private static string RenderToDataUrl(string text, PixelPressOptions cfg)
    {
        var lines = WrapLines(text, cfg.MaxColumns);
        var fontSize = Math.Max(6, cfg.FontSize);

        using var typeface =
            SKTypeface.FromFamilyName("Consolas")
            ?? SKTypeface.FromFamilyName("Courier New")
            ?? SKTypeface.FromFamilyName("monospace")
            ?? SKTypeface.Default;

        using var paint = new SKPaint
        {
            Color = SKColors.Black,
            IsAntialias = true,
            Typeface = typeface,
            TextSize = fontSize,
        };

        var charWidth = paint.MeasureText("M");
        var metrics = paint.FontMetrics;
        var lineHeight = MathF.Ceiling(metrics.Descent - metrics.Ascent + metrics.Leading);
        const int padding = 8;

        var maxLineLen = 0;
        foreach (var line in lines)
        {
            if (line.Length > maxLineLen)
            {
                maxLineLen = line.Length;
            }
        }

        var width = Math.Max(16, (int)MathF.Ceiling(maxLineLen * charWidth) + padding * 2);
        var height = Math.Max(16, (int)(lines.Count * lineHeight) + padding * 2);

        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);

        var y = padding - metrics.Ascent;
        foreach (var line in lines)
        {
            if (line.Length > 0)
            {
                canvas.DrawText(line, padding, y, paint);
            }
            y += lineHeight;
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return "data:image/png;base64," + Convert.ToBase64String(data.ToArray());
    }

    private static List<string> WrapLines(string text, int maxColumns)
    {
        maxColumns = Math.Max(1, maxColumns);
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var result = new List<string>();

        foreach (var raw in normalized.Split('\n'))
        {
            var line = raw.Replace("\t", "    ");
            if (line.Length == 0)
            {
                result.Add(string.Empty);
                continue;
            }

            for (var i = 0; i < line.Length; i += maxColumns)
            {
                result.Add(line.Substring(i, Math.Min(maxColumns, line.Length - i)));
            }
        }

        if (result.Count == 0)
        {
            result.Add(string.Empty);
        }

        return result;
    }
}
