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

        Assert.Equal("gpt-5.6-sol", result["model"]!.GetValue<string>());
        Assert.Equal(256, result["max_output_tokens"]!.GetValue<int>());
        Assert.Null(result["max_tokens"]);
        Assert.Null(result["messages"]);

        var input = (JsonArray)result["input"]!;
        Assert.Equal(2, input.Count);
        Assert.Equal("message", input[0]!["type"]!.GetValue<string>());
        Assert.Equal("system", input[0]!["role"]!.GetValue<string>());
        Assert.Equal("input_text", input[0]!["content"]![0]!["type"]!.GetValue<string>());
        Assert.Equal("be brief", input[0]!["content"]![0]!["text"]!.GetValue<string>());
    }

    [Fact]
    public void Drops_fields_the_responses_api_rejects()
    {
        var request = new JsonObject
        {
            ["model"] = "gpt-5.6-sol",
            ["stop"] = new JsonArray { "END" },
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
            ["messages"] = new JsonArray()
        };

        var result = ResponsesApiTranslator.ToResponsesRequest(request);

        Assert.Null(result["stop"]);
        Assert.Null(result["stream_options"]);
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

        Assert.Equal("high", result["reasoning"]!["effort"]!.GetValue<string>());
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

        Assert.Equal("input_text", content[0]!["type"]!.GetValue<string>());
        Assert.Equal("input_image", content[1]!["type"]!.GetValue<string>());
        Assert.Equal("data:image/png;base64,AAAA", content[1]!["image_url"]!.GetValue<string>());
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

        Assert.Equal("function", tool["type"]!.GetValue<string>());
        Assert.Equal("get_weather", tool["name"]!.GetValue<string>());
        Assert.Equal("Look up weather", tool["description"]!.GetValue<string>());
        Assert.Null(tool["function"]);
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

        Assert.Equal(3, input.Count);
        Assert.Equal("message", input[0]!["type"]!.GetValue<string>());
        Assert.Equal("output_text", input[0]!["content"]![0]!["type"]!.GetValue<string>());

        Assert.Equal("function_call", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("call_1", input[1]!["call_id"]!.GetValue<string>());
        Assert.Equal("get_weather", input[1]!["name"]!.GetValue<string>());

        Assert.Equal("function_call_output", input[2]!["type"]!.GetValue<string>());
        Assert.Equal("call_1", input[2]!["call_id"]!.GetValue<string>());
        Assert.Equal("sunny", input[2]!["output"]!.GetValue<string>());
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

        Assert.Single(input);
        Assert.Equal("function_call", input[0]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Parses_text_delta_event()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.output_text.delta","delta":"Hello"}""");

        Assert.NotNull(chunk);
        Assert.Equal("Hello", chunk!.ContentDelta);
    }

    [Fact]
    public void Ignores_events_with_nothing_to_deliver()
    {
        Assert.Null(ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.reasoning_summary_text.delta","delta":"thinking"}"""));
        Assert.Null(ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.created","response":{"id":"resp_1"}}"""));
    }

    [Fact]
    public void Emits_complete_tool_call_on_item_done()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """
            {"type":"response.output_item.done","output_index":2,
             "item":{"type":"function_call","call_id":"call_9","name":"get_weather","arguments":"{\"city\":\"Berlin\"}"}}
            """);

        Assert.NotNull(chunk);
        var call = chunk!.ToolCalls!.Single();
        Assert.Equal(2, call.GetProperty("index").GetInt32());
        Assert.Equal("call_9", call.GetProperty("id").GetString());
        Assert.Equal("function", call.GetProperty("type").GetString());
        Assert.Equal("get_weather", call.GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("{\"city\":\"Berlin\"}", call.GetProperty("function").GetProperty("arguments").GetString());
    }

    [Fact]
    public void Ignores_non_function_item_done_events()
    {
        Assert.Null(ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.output_item.done","output_index":0,"item":{"type":"message","role":"assistant"}}"""));
    }

    [Fact]
    public void Reports_usage_and_finish_reason_on_completion()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """
            {"type":"response.completed","response":{"status":"completed",
             "output":[{"type":"message"}],"usage":{"input_tokens":11,"output_tokens":7}}}
            """);

        Assert.NotNull(chunk);
        Assert.Equal("stop", chunk!.FinishReason);
        Assert.Equal(11, chunk.PromptTokens);
        Assert.Equal(7, chunk.CompletionTokens);
    }

    [Fact]
    public void Reports_tool_calls_finish_reason_when_output_has_function_call()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """
            {"type":"response.completed","response":{"status":"completed",
             "output":[{"type":"function_call","call_id":"c","name":"f","arguments":"{}"}]}}
            """);

        Assert.Equal("tool_calls", chunk!.FinishReason);
    }

    [Fact]
    public void Reports_length_finish_reason_when_incomplete()
    {
        var chunk = ResponsesApiTranslator.ParseStreamEvent(
            """{"type":"response.incomplete","response":{"status":"incomplete","output":[]}}""");

        Assert.Equal("length", chunk!.FinishReason);
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

        Assert.Equal("Hi there", chunk.ContentDelta);
        Assert.Equal("tool_calls", chunk.FinishReason);
        Assert.Equal(5, chunk.PromptTokens);
        Assert.Equal(2, chunk.CompletionTokens);
        Assert.Equal("call_3", chunk.ToolCalls!.Single().GetProperty("id").GetString());
    }
}
