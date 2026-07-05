using System.Text.Json.Nodes;
using AiProxy.Pipeline.Middlewares;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

public class PixelPressMiddlewareTests
{
    private static readonly string LongText =
        string.Join('\n', Enumerable.Repeat("the quick brown fox jumps over the lazy dog.", 60));

    private static PixelPressMiddleware Create(PixelPressOptions options) =>
        new(Options.Create(new AiProxyOptions { PixelPress = options }));

    private static PixelPressOptions EnabledOptions() => new()
    {
        Enabled = true,
        Roles = new List<string> { "system", "user" },
        MinCharacters = 100,
        FontSize = 14,
        MaxColumns = 120,
        IncludeHint = true,
    };

    private static JsonObject? MessageContentImagePart(JsonObject request, int messageIndex = 0)
    {
        var messages = request["messages"] as JsonArray;
        var msg = messages?[messageIndex] as JsonObject;
        var parts = msg?["content"] as JsonArray;
        return parts?
            .OfType<JsonObject>()
            .FirstOrDefault(p => p["type"]?.GetValue<string>() == "image_url");
    }

    [Fact]
    public async Task Disabled_passes_request_through_untouched()
    {
        var options = EnabledOptions();
        options.Enabled = false;
        var middleware = Create(options);

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal(LongText, TestPipeline.Content(result));
    }

    [Fact]
    public async Task Renders_long_user_message_to_png_image_part()
    {
        var middleware = Create(EnabledOptions());

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        var imagePart = MessageContentImagePart(result);
        Assert.NotNull(imagePart);

        var url = imagePart!["image_url"]!["url"]!.GetValue<string>();
        const string prefix = "data:image/png;base64,";
        Assert.StartsWith(prefix, url);

        // The decoded payload must be a real PNG (magic bytes 89 50 4E 47).
        var bytes = Convert.FromBase64String(url[prefix.Length..]);
        Assert.True(bytes.Length > 8);
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);
    }

    [Fact]
    public async Task Prepends_hint_text_part_when_enabled()
    {
        var middleware = Create(EnabledOptions());

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        var parts = (result["messages"] as JsonArray)?[0]?["content"] as JsonArray;
        Assert.NotNull(parts);
        var firstText = parts![0] as JsonObject;
        Assert.Equal("text", firstText!["type"]!.GetValue<string>());
        Assert.Contains("rendered as pixels", firstText["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Omits_hint_when_disabled()
    {
        var options = EnabledOptions();
        options.IncludeHint = false;
        var middleware = Create(options);

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        var parts = (result["messages"] as JsonArray)?[0]?["content"] as JsonArray;
        Assert.NotNull(parts);
        Assert.All(parts!, p => Assert.Equal("image_url", (p as JsonObject)!["type"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Skips_content_below_min_characters()
    {
        var options = EnabledOptions();
        options.MinCharacters = 100_000;
        var middleware = Create(options);

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal(LongText, TestPipeline.Content(result));
    }

    [Fact]
    public async Task Only_renders_configured_roles()
    {
        var options = EnabledOptions();
        options.Roles = new List<string> { "user" };
        var middleware = Create(options);

        var request = TestPipeline.Request("system", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal(LongText, TestPipeline.Content(result));
    }

    [Fact]
    public async Task Renders_long_text_part_inside_array_content()
    {
        var request = new JsonObject
        {
            ["model"] = "gpt-4o",
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "short leading note" },
                        new JsonObject { ["type"] = "text", ["text"] = LongText },
                    },
                },
            },
        };

        var middleware = Create(EnabledOptions());
        var result = await TestPipeline.RunAsync(middleware, request);

        var parts = (result["messages"] as JsonArray)?[0]?["content"] as JsonArray;
        Assert.NotNull(parts);
        Assert.Contains(parts!.OfType<JsonObject>(), p => p["type"]?.GetValue<string>() == "image_url");
        // The short leading note stays as text.
        Assert.Contains(parts!.OfType<JsonObject>(),
            p => p["type"]?.GetValue<string>() == "text" && p["text"]?.GetValue<string>() == "short leading note");
    }
}
