using System.Text.Json;
using AiProxy.Proxy;

namespace AiProxy.Tests;

/// <summary>
/// Covers the native /api/chat translation used by the Ollama VS Code extension, which
/// speaks Ollama's own wire format rather than the OpenAI-shaped /v1/chat/completions
/// surface the deprecated built-in provider used.
/// </summary>
public class OllamaChatTranslationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static OllamaEndpoints.OllamaChatRequest Parse(string json) =>
        JsonSerializer.Deserialize<OllamaEndpoints.OllamaChatRequest>(json, JsonOptions)!;

    private static JsonElement Element(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Binds_snake_case_fields_from_the_wire()
    {
        var request = Parse("""
        {
          "model": "gpt-5",
          "messages": [
            { "role": "tool", "content": "42", "tool_call_id": "call_abc", "tool_name": "calc" }
          ],
          "options": { "top_p": 0.5, "num_predict": 128 }
        }
        """);

        var message = request.Messages![0];
        message.ToolCallId.Should().Be("call_abc");
        message.ToolName.Should().Be("calc");
        request.Options!.TopP.Should().Be(0.5);
        request.Options!.NumPredict.Should().Be(128);
    }

    [Fact]
    public void Tool_result_carries_the_call_id_upstream()
    {
        var request = Parse("""
        { "messages": [ { "role": "tool", "content": "42", "tool_call_id": "call_abc" } ] }
        """);

        var converted = OllamaEndpoints.ConvertMessage(request.Messages![0]);

        ((string?)converted["role"]).Should().Be("tool");
        ((string?)converted["tool_call_id"]).Should().Be("call_abc");
    }

    [Fact]
    public void Assistant_tool_call_gains_id_type_and_string_arguments()
    {
        var request = Parse("""
        {
          "messages": [
            {
              "role": "assistant",
              "content": "",
              "tool_calls": [
                { "id": "call_1", "function": { "name": "read_file", "arguments": { "path": "a.txt" } } }
              ]
            }
          ]
        }
        """);

        var converted = OllamaEndpoints.ConvertMessage(request.Messages![0]);
        var call = converted["tool_calls"]!.AsArray()[0]!;

        ((string?)call["id"]).Should().Be("call_1");
        ((string?)call["type"]).Should().Be("function");
        ((string?)call["function"]!["name"]).Should().Be("read_file");

        // The OpenAI wire format requires arguments as a JSON string, not an object.
        var arguments = (string?)call["function"]!["arguments"];
        arguments.Should().NotBeNull();
        JsonDocument.Parse(arguments).RootElement.GetProperty("path").GetString().Should().Be("a.txt");
    }

    [Fact]
    public void Synthesizes_an_id_when_the_client_omits_one()
    {
        var request = Parse("""
        {
          "messages": [
            { "role": "assistant", "tool_calls": [ { "function": { "name": "ls", "arguments": {} } } ] }
          ]
        }
        """);

        var converted = OllamaEndpoints.ConvertMessage(request.Messages![0]);
        var id = (string?)converted["tool_calls"]!.AsArray()[0]!["id"];

        string.IsNullOrEmpty(id).Should().BeFalse();
    }

    [Fact]
    public void Reassembles_streamed_tool_call_fragments()
    {
        var accumulator = new OllamaEndpoints.ToolCallAccumulator();
        accumulator.Add(Element("""{"index":0,"id":"call_1","function":{"name":"read_file","arguments":""}}"""));
        accumulator.Add(Element("""{"index":0,"function":{"arguments":"{\"path\":"}}"""));
        accumulator.Add(Element("""{"index":0,"function":{"arguments":"\"a.txt\"}"}}"""));

        var built = accumulator.Build();
        var json = JsonSerializer.SerializeToElement(built, JsonOptions)[0];

        json.GetProperty("id").GetString().Should().Be("call_1");
        json.GetProperty("function").GetProperty("name").GetString().Should().Be("read_file");
        // VS Code hands arguments to the tool as its input object, so it must be parsed JSON.
        var arguments = json.GetProperty("function").GetProperty("arguments");
        arguments.ValueKind.Should().Be(JsonValueKind.Object);
        arguments.GetProperty("path").GetString().Should().Be("a.txt");
    }

    [Fact]
    public void Keeps_parallel_tool_calls_separate()
    {
        var accumulator = new OllamaEndpoints.ToolCallAccumulator();
        accumulator.Add(Element("""{"index":0,"id":"a","function":{"name":"first","arguments":"{}"}}"""));
        accumulator.Add(Element("""{"index":1,"id":"b","function":{"name":"second","arguments":"{}"}}"""));

        var built = accumulator.Build();
        var json = JsonSerializer.SerializeToElement(built, JsonOptions);

        json.GetArrayLength().Should().Be(2);
        json[0].GetProperty("function").GetProperty("name").GetString().Should().Be("first");
        json[1].GetProperty("function").GetProperty("name").GetString().Should().Be("second");
    }

    [Fact]
    public void Falls_back_to_an_empty_object_for_unparsable_arguments()
    {
        var accumulator = new OllamaEndpoints.ToolCallAccumulator();
        accumulator.Add(Element("""{"index":0,"id":"a","function":{"name":"x","arguments":"{not json"}}"""));

        var json = JsonSerializer.SerializeToElement(accumulator.Build(), JsonOptions)[0];
        var arguments = json.GetProperty("function").GetProperty("arguments");

        arguments.ValueKind.Should().Be(JsonValueKind.Object);
        arguments.EnumerateObject().Count().Should().Be(0);
    }

    [Fact]
    public void Drops_fragments_that_never_named_a_function()
    {
        var accumulator = new OllamaEndpoints.ToolCallAccumulator();
        accumulator.Add(Element("""{"index":0,"function":{"arguments":"{}"}}"""));

        accumulator.Build().Should().BeEmpty();
    }

    [Fact]
    public void Images_still_become_content_parts()
    {
        var request = Parse("""
        { "messages": [ { "role": "user", "content": "what is this?", "images": ["iVBORw0KGgo="] } ] }
        """);

        var converted = OllamaEndpoints.ConvertMessage(request.Messages![0]);
        var parts = converted["content"]!.AsArray();

        ((string?)parts[0]!["type"]).Should().Be("text");
        ((string?)parts[1]!["type"]).Should().Be("image_url");
        ((string?)parts[1]!["image_url"]!["url"]).Should().StartWith("data:image/png;base64,");
    }
}
