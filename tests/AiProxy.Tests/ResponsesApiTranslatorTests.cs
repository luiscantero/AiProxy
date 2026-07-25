using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Pipeline;

namespace AiProxy.Tests;

public class ResponsesApiTranslatorTests
{
    [Fact]
    public void Maps_messages_to_input_items()
    {
        var request = new JsonObject
        {
            ["model"] = "gpt-5.6-sol",
            ["stream"] = true,
            ["max_tokens"] = 256,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = "be brief" },
                new JsonObject { ["role"] = "user", ["content"] = "hi" }
            }
        };

        var result = ResponsesApiTranslator.ToResponsesRequest(request);

        result["model"]!.GetValue<string>().Should().Be("gpt-5.6-sol");
        result["max_output_tokens"]!.GetValue<int>().Should().Be(256);
        result["max_tokens"].Should().BeNull();
        result["messages"].Should().BeNull();

        var input = (JsonArray)result["input"]!;
        input.Count.Should().Be(2);
        input[0]!["type"]!.GetValue<string>().Should().Be("message");
        input[0]!["role"]!.GetValue<string>().Should().Be("system");
        input[0]!["content"]![0]!["type"]!.GetValue<string>().Should().Be("input_text");
        input[0]!["content"]![0]!["text"]!.GetValue<string>().Should().Be("be brief");
    }

    [Fact]
    public void Drops_fields_the_responses_api_rejects()
    {
        var request = new JsonObject
        {
            ["model"] = "gpt-5.6-sol",
            ["temperature"] = 0.7,
            ["top_p"] = 0.9,
            ["stop"] = new JsonArray { "END" },
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = new JsonArray()
        };

        var result = ResponsesApiTranslator.ToResponsesRequest(request);

        result["temperature"].Should().BeNull();
        result["top_p"].Should().BeNull();
        result["stop"].Should().BeNull();
        result["stream_options"].Should().BeNull();
    }

    [Fact]
    public void Maps_reasoning_effort_into_nested_object()
    {
        var request = new JsonObject
        {
            ["model"] = "gpt-5.6-sol",
            ["reasoning_effort"] = "high",
            ["messages"] = new JsonArray()
        };

        var result = ResponsesApiTranslator.ToResponsesRequest(request);

        result["reasoning"]!["effort"]!.GetValue<string>().Should().Be("high");
    }

    [Fact]
    public void Converts_image_parts_to_input_image()
    {
        var request = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "text", ["text"] = "what is this?" },
                        new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = "data:image/png;base64,AAAA" }
                        }
                    }
                }
            }
        };

        var content = (JsonArray)ResponsesApiTranslator.ToResponsesRequest(request)["input"]![0]!["content"]!;

        content[0]!["type"]!.GetValue<string>().Should().Be("input_text");
        content[1]!["type"]!.GetValue<string>().Should().Be("input_image");
        content[1]!["image_url"]!.GetValue<string>().Should().Be("data:image/png;base64,AAAA");
    }

    [Fact]
    public void Flattens_tool_definitions()
    {
        var request = new JsonObject
        {
            ["messages"] = new JsonArray(),
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = "get_weather",
                        ["description"] = "Look up weather",
                        ["parameters"] = new JsonObject { ["type"] = "object" }
                    }
                }
            }
        };

        var tool = ResponsesApiTranslator.ToResponsesRequest(request)["tools"]![0]!;

        tool["type"]!.GetValue<string>().Should().Be("function");
        tool["name"]!.GetValue<string>().Should().Be("get_weather");
        tool["description"]!.GetValue<string>().Should().Be("Look up weather");
        tool["function"].Should().BeNull();
    }

    [Fact]
    public void Splits_assistant_tool_calls_into_function_call_items()
    {
        var request = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "calling a tool",
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "call_1",
                            ["type"] = "function",
                            ["function"] = new JsonObject
                            {
                                ["name"] = "get_weather",
                                ["arguments"] = "{\"city\":\"Berlin\"}"
                            }
                        }
                    }
                },
                new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = "call_1",
                    ["content"] = "sunny"
                }
            }
        };

        var input = (JsonArray)ResponsesApiTranslator.ToResponsesRequest(request)["input"]!;

        input.Count.Should().Be(3);
        input[0]!["type"]!.GetValue<string>().Should().Be("message");
        input[0]!["content"]![0]!["type"]!.GetValue<string>().Should().Be("output_text");

        input[1]!["type"]!.GetValue<string>().Should().Be("function_call");
        input[1]!["call_id"]!.GetValue<string>().Should().Be("call_1");
        input[1]!["name"]!.GetValue<string>().Should().Be("get_weather");

        input[2]!["type"]!.GetValue<string>().Should().Be("function_call_output");
        input[2]!["call_id"]!.GetValue<string>().Should().Be("call_1");
        input[2]!["output"]!.GetValue<string>().Should().Be("sunny");
    }

    [Fact]
    public void Omits_message_item_when_assistant_has_only_tool_calls()
    {
        var request = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "assistant",
                    ["content"] = "",
                    ["tool_calls"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["id"] = "call_1",
                            ["function"] = new JsonObject { ["name"] = "f", ["arguments"] = "{}" }
                        }
                    }
                }
            }
        };

        var input = (JsonArray)ResponsesApiTranslator.ToResponsesRequest(request)["input"]!;

        input.Should().ContainSingle();
        input[0]!["type"]!.GetValue<string>().Should().Be("function_call");
    }

    [Fact]
    public void Parses_text_delta_event()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.output_text.delta","delta":"Hello"}""");

        chunk.Should().NotBeNull();
        chunk!.ContentDelta.Should().Be("Hello");
    }

    [Fact]
    public void Ignores_events_with_nothing_to_deliver()
    {
        ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.reasoning_summary_text.delta","delta":"thinking"}""").Should().BeNull();
        ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.created","response":{"id":"resp_1"}}""").Should().BeNull();
    }

    [Fact]
    public void Emits_complete_tool_call_on_item_done()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """
            {"type":"response.output_item.done","output_index":2,
             "item":{"type":"function_call","call_id":"call_9","name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}}
            """);

        chunk.Should().NotBeNull();
        var call = chunk!.ToolCalls!.Single();
        call.GetProperty("index").GetInt32().Should().Be(2);
        call.GetProperty("id").GetString().Should().Be("call_9");
        call.GetProperty("type").GetString().Should().Be("function");
        call.GetProperty("function").GetProperty("name").GetString().Should().Be("get_weather");
        call.GetProperty("function").GetProperty("arguments").GetString().Should().Be("{\"city\":\"Berlin\"}");
    }

    [Fact]
    public void Ignores_non_function_item_done_events()
    {
        ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","role":"assistant"}}""").Should().BeNull();
    }

    [Fact]
    public void Reports_usage_and_finish_reason_on_completion()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """
            {"type":"response.completed","response":{"status":"completed",
             "output":[{"type":"message"}],"usage":{"input_tokens":11,"output_tokens":7}}}
            """);

        chunk.Should().NotBeNull();
        chunk!.FinishReason.Should().Be("stop");
        chunk.PromptTokens.Should().Be(11);
        chunk.CompletionTokens.Should().Be(7);
    }

    [Fact]
    public void Reports_tool_calls_finish_reason_when_output_has_function_call()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """
            {"type":"response.completed","response":{"status":"completed",
             "output":[{"type":"function_call","call_id":"c","name":"f","arguments":"{}"}]}}
            """);

        chunk!.FinishReason.Should().Be("tool_calls");
    }

    [Fact]
    public void Reports_length_finish_reason_when_incomplete()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.incomplete","response":{"status":"incomplete","output":[]}}""");

        chunk!.FinishReason.Should().Be("length");
    }

    [Fact]
    public void Parses_non_streaming_response()
    {
        using var doc = JsonDocument.Parse(
            """
            {"id":"resp_1","status":"completed",
             "output":[
               {"type":"reasoning","summary":[]},
               {"type":"message","role":"assistant","content":[{"type":"output_text","text":"Hi there"}]},
               {"type":"function_call","call_id":"call_3","name":"f","arguments":"{}"}
             ],
             "usage":{"input_tokens":5,"output_tokens":2}}
            """);

        var chunk = ResponsesApiTranslator.ParseNonStreamingResponse(doc.RootElement);

        chunk.ContentDelta.Should().Be("Hi there");
        chunk.FinishReason.Should().Be("tool_calls");
        chunk.PromptTokens.Should().Be(5);
        chunk.CompletionTokens.Should().Be(2);
        chunk.ToolCalls!.Single().GetProperty("id").GetString().Should().Be("call_3");
    }
}
