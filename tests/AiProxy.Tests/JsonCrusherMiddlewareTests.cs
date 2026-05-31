using System.Text.Json.Nodes;
using AiProxy.Pipeline.Middlewares;

namespace AiProxy.Tests;

public class JsonCrusherMiddlewareTests
{
    private readonly JsonCrusherMiddleware _middleware = new();

    [Fact]
    public async Task Minifies_embedded_pretty_json_in_string_content()
    {
        const string pretty = "Here is the payload:\n{\n  \"name\": \"ada\",\n  \"age\": 36\n}\nThanks";
        var request = TestPipeline.Request("user", pretty);

        var result = await TestPipeline.RunAsync(_middleware, request);

        var content = TestPipeline.Content(result)!;
        Assert.Contains("{\"name\":\"ada\",\"age\":36}", content);
        Assert.Contains("Here is the payload:", content);
        Assert.Contains("Thanks", content);
        Assert.True(content.Length < pretty.Length);
    }

    [Fact]
    public async Task Is_lossless_embedded_json_still_parses_to_same_value()
    {
        const string pretty = "result = [\n  { \"id\": 1 },\n  { \"id\": 2 }\n]";
        var request = TestPipeline.Request("user", pretty);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        var start = content.IndexOf('[');
        var json = content[start..];
        var parsed = JsonNode.Parse(json) as JsonArray;
        Assert.NotNull(parsed);
        Assert.Equal(2, parsed!.Count);
        Assert.Equal(1, parsed[0]!["id"]!.GetValue<int>());
        Assert.Equal(2, parsed[1]!["id"]!.GetValue<int>());
    }

    [Fact]
    public async Task Leaves_plain_text_without_json_unchanged()
    {
        const string text = "Just a normal sentence with { an unbalanced brace and no json.";
        var request = TestPipeline.Request("user", text);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request));

        Assert.Equal(text, content);
    }

    [Fact]
    public async Task Does_not_corrupt_braces_inside_string_literals()
    {
        // The "}" inside the string value must not be treated as the object's closing brace.
        const string original = "{\n  \"template\": \"hello {name}\"\n}";
        var request = TestPipeline.Request("user", original);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        var parsed = JsonNode.Parse(content)!;
        Assert.Equal("hello {name}", parsed["template"]!.GetValue<string>());
    }

    [Fact]
    public async Task Compacts_text_in_array_content_parts()
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
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = "data: {\n  \"a\": 1,\n  \"b\": 2\n}",
                        },
                    },
                },
            },
        };

        var result = await TestPipeline.RunAsync(_middleware, request);

        var text = result["messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("{\"a\":1,\"b\":2}", text);
    }

    [Fact]
    public async Task Fails_open_when_request_has_no_messages()
    {
        var request = new JsonObject { ["model"] = "gpt-4o" };

        var result = await TestPipeline.RunAsync(_middleware, request);

        Assert.Null(result["messages"]);
    }
}
