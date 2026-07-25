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

        TestPipeline.Content(result).Should().Be(LongText);
    }

    [Fact]
    public async Task Renders_long_user_message_to_png_image_part()
    {
        var middleware = Create(EnabledOptions());

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        var imagePart = MessageContentImagePart(result);
        imagePart.Should().NotBeNull();

        var url = imagePart!["image_url"]!["url"]!.GetValue<string>();
        const string prefix = "data:image/png;base64,";
        url.Should().StartWith(prefix);

        // The decoded payload must be a real PNG (magic bytes 89 50 4E 47).
        var bytes = Convert.FromBase64String(url[prefix.Length..]);
        (bytes.Length > 8).Should().BeTrue();
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be(0x50);
        bytes[2].Should().Be(0x4E);
        bytes[3].Should().Be(0x47);
    }

    [Fact]
    public async Task Prepends_hint_text_part_when_enabled()
    {
        var middleware = Create(EnabledOptions());

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        var parts = (result["messages"] as JsonArray)?[0]?["content"] as JsonArray;
        parts.Should().NotBeNull();
        var firstText = parts![0] as JsonObject;
        firstText!["type"]!.GetValue<string>().Should().Be("text");
        firstText["text"]!.GetValue<string>().Should().Contain("rendered as pixels");
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
        parts.Should().NotBeNull();
        parts!.Should().AllSatisfy(p => ((p as JsonObject)!["type"]!.GetValue<string>()).Should().Be("image_url"));
    }

    [Fact]
    public async Task Skips_content_below_min_characters()
    {
        var options = EnabledOptions();
        options.MinCharacters = 100_000;
        var middleware = Create(options);

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        TestPipeline.Content(result).Should().Be(LongText);
    }

    [Fact]
    public async Task Only_renders_configured_roles()
    {
        var options = EnabledOptions();
        options.Roles = new List<string> { "user" };
        var middleware = Create(options);

        var request = TestPipeline.Request("system", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        TestPipeline.Content(result).Should().Be(LongText);
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
        parts.Should().NotBeNull();
        parts!.OfType<JsonObject>().Should().Contain(p => p["type"]!.GetValue<string>() == "image_url");
        // The short leading note stays as text.
        parts!.OfType<JsonObject>().Should().Contain(
            p => p["type"]!.GetValue<string>() == "text" && p["text"]!.GetValue<string>() == "short leading note");
    }
}
