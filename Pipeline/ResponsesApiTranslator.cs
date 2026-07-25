using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiProxy.Pipeline;

/// <summary>
/// Translates between the OpenAI chat-completions wire format used everywhere inside this
/// proxy and the OpenAI Responses API.
///
/// <para>
/// Some upstream models (Copilot's <c>gpt-5.6-sol</c>, <c>gpt-5.6-luna</c>, <c>gpt-5.6-terra</c>,
/// <c>gpt-5.5</c>, ...) advertise only <c>/responses</c> and reject <c>/chat/completions</c>.
/// Rather than teach every surface and middleware a second wire format, the whole pipeline keeps
/// speaking chat-completions and this translator converts at the very last moment, inside
/// <see cref="UpstreamChatInvoker"/>. Both directions are covered: request shape on the way out,
/// streamed events and the non-streaming response on the way back.
/// </para>
/// </summary>
public static class ResponsesApiTranslator
{
    /// <summary>
    /// Converts a chat-completions request body into the Responses API equivalent.
    /// Fields are whitelisted rather than copied wholesale, because the Responses API rejects
    /// unknown properties (<c>stop</c> and <c>stream_options</c> have no equivalent and are dropped).
    /// </summary>
    public static JsonObject ToResponsesRequest(JsonObject chatRequest)
    {
        var responses = new JsonObject();

        if (chatRequest["model"] is { } model) responses["model"] = model.DeepClone();
        if (chatRequest["stream"] is { } stream) responses["stream"] = stream.DeepClone();
        if (chatRequest["temperature"] is { } temperature) responses["temperature"] = temperature.DeepClone();
        if (chatRequest["top_p"] is { } topP) responses["top_p"] = topP.DeepClone();

        // The output cap was renamed by the Responses API.
        if ((chatRequest["max_completion_tokens"] ?? chatRequest["max_tokens"]) is { } maxTokens)
        {
            responses["max_output_tokens"] = maxTokens.DeepClone();
        }

        // Reasoning effort moves from a flat field into a nested object.
        if (chatRequest["reasoning_effort"] is { } effort)
        {
            responses["reasoning"] = new JsonObject { ["effort"] = effort.DeepClone() };
        }

        var input = new JsonArray();
        if (chatRequest["messages"] is JsonArray messages)
        {
            foreach (var message in messages.OfType<JsonObject>())
            {
                AppendInputItems(input, message);
            }
        }
        responses["input"] = input;

        if (chatRequest["tools"] is JsonArray tools && tools.Count > 0)
        {
            var converted = new JsonArray();
            foreach (var tool in tools.OfType<JsonObject>())
            {
                converted.Add(ConvertTool(tool));
            }
            responses["tools"] = converted;
        }

        if (chatRequest["tool_choice"] is { } toolChoice)
        {
            responses["tool_choice"] = ConvertToolChoice(toolChoice);
        }

        return responses;
    }

    /// <summary>
    /// Expands one chat message into the (possibly several) Responses input items it maps to.
    /// An assistant turn carrying tool calls becomes a message item plus one
    /// <c>function_call</c> item each, because Responses models tool calls as top-level items
    /// rather than a property of the message.
    /// </summary>
    private static void AppendInputItems(JsonArray input, JsonObject message)
    {
        var role = message["role"]?.GetValue<string>() ?? "user";

        if (role == "tool")
        {
            input.Add(new JsonObject
            {
                ["type"] = "function_call_output",
                ["call_id"] = message["tool_call_id"]?.GetValue<string>() ?? "",
                ["output"] = ContentToPlainText(message["content"])
            });
            return;
        }

        var content = message["content"];
        if (!IsEmptyContent(content))
        {
            input.Add(new JsonObject
            {
                ["type"] = "message",
                ["role"] = role,
                ["content"] = ConvertContent(content!, role)
            });
        }

        if (role == "assistant" && message["tool_calls"] is JsonArray toolCalls)
        {
            foreach (var call in toolCalls.OfType<JsonObject>())
            {
                var function = call["function"] as JsonObject;
                input.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = call["id"]?.GetValue<string>() ?? "",
                    ["name"] = function?["name"]?.GetValue<string>() ?? "",
                    ["arguments"] = function?["arguments"]?.GetValue<string>() ?? "{}"
                });
            }
        }
    }

    /// <summary>
    /// Converts message content into Responses content parts. Text parts are role-sensitive
    /// (<c>output_text</c> for assistant turns, <c>input_text</c> otherwise) and images move
    /// from the nested <c>image_url.url</c> shape to a flat <c>input_image</c> part.
    /// </summary>
    private static JsonArray ConvertContent(JsonNode content, string role)
    {
        var textType = role == "assistant" ? "output_text" : "input_text";
        var parts = new JsonArray();

        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            parts.Add(new JsonObject { ["type"] = textType, ["text"] = text });
            return parts;
        }

        if (content is JsonArray array)
        {
            foreach (var part in array.OfType<JsonObject>())
            {
                switch (part["type"]?.GetValue<string>())
                {
                    case "text":
                        parts.Add(new JsonObject
                        {
                            ["type"] = textType,
                            ["text"] = part["text"]?.GetValue<string>() ?? ""
                        });
                        break;

                    case "image_url":
                        var url = part["image_url"] switch
                        {
                            JsonObject image => image["url"]?.GetValue<string>(),
                            JsonValue direct when direct.TryGetValue<string>(out var raw) => raw,
                            _ => null
                        };
                        if (!string.IsNullOrEmpty(url))
                        {
                            parts.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = url });
                        }
                        break;

                    // Already in Responses shape (e.g. a middleware built it directly).
                    case "input_text":
                    case "output_text":
                    case "input_image":
                        parts.Add(part.DeepClone());
                        break;
                }
            }
        }

        return parts;
    }

    private static bool IsEmptyContent(JsonNode? content) => content switch
    {
        null => true,
        JsonValue value => !value.TryGetValue<string>(out var text) || text.Length == 0,
        JsonArray array => array.Count == 0,
        _ => false
    };

    private static string ContentToPlainText(JsonNode? content)
    {
        if (content is JsonValue value && value.TryGetValue<string>(out var text))
        {
            return text;
        }

        if (content is JsonArray array)
        {
            var builder = new StringBuilder();
            foreach (var part in array.OfType<JsonObject>())
            {
                if (part["text"]?.GetValue<string>() is { } partText)
                {
                    builder.Append(partText);
                }
            }
            return builder.ToString();
        }

        return "";
    }

    /// <summary>Responses flattens the nested <c>function</c> envelope onto the tool itself.</summary>
    private static JsonNode ConvertTool(JsonObject tool)
    {
        if (tool["function"] is not JsonObject function)
        {
            return tool.DeepClone();
        }

        var converted = new JsonObject
        {
            ["type"] = "function",
            ["name"] = function["name"]?.DeepClone(),
            ["parameters"] = function["parameters"]?.DeepClone()
                             ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() }
        };
        if (function["description"] is { } description) converted["description"] = description.DeepClone();
        if (function["strict"] is { } strict) converted["strict"] = strict.DeepClone();
        return converted;
    }

    private static JsonNode ConvertToolChoice(JsonNode toolChoice)
    {
        if (toolChoice is JsonObject obj && obj["function"] is JsonObject function)
        {
            return new JsonObject { ["type"] = "function", ["name"] = function["name"]?.DeepClone() };
        }
        return toolChoice.DeepClone();
    }

    /// <summary>
    /// Parses one Responses SSE payload into a normalized chunk, or null for events that carry
    /// nothing the downstream surfaces need (reasoning summaries, lifecycle noise, ...).
    /// </summary>
    public static ChatResponseChunk? ParseStreamEvent(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var type = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : null;

        switch (type)
        {
            case "response.output_text.delta":
                return root.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.String
                    ? new ChatResponseChunk { ContentDelta = delta.GetString() }
                    : null;

            // Tool calls are emitted whole on item completion rather than reassembled from
            // argument deltas: downstream surfaces pass tool calls through verbatim, and the
            // non-streaming aggregator concatenates them, so partial fragments would corrupt it.
            case "response.output_item.done":
            {
                if (!root.TryGetProperty("item", out var item)
                    || item.ValueKind != JsonValueKind.Object
                    || !IsFunctionCall(item))
                {
                    return null;
                }

                var index = root.TryGetProperty("output_index", out var outputIndex)
                            && outputIndex.TryGetInt32(out var parsed)
                    ? parsed
                    : 0;
                return new ChatResponseChunk { ToolCalls = new[] { ToChatToolCall(item, index) } };
            }

            case "response.completed":
            case "response.incomplete":
            case "response.failed":
            {
                var chunk = new ChatResponseChunk();
                if (root.TryGetProperty("response", out var response) && response.ValueKind == JsonValueKind.Object)
                {
                    ApplyUsage(response, chunk);
                    chunk.FinishReason = FinishReasonFor(response);
                }
                else
                {
                    chunk.FinishReason = "stop";
                }
                return chunk;
            }

            default:
                return null;
        }
    }

    /// <summary>Collapses a complete (non-streamed) Responses body into a single chunk.</summary>
    public static ChatResponseChunk ParseNonStreamingResponse(JsonElement root)
    {
        var chunk = new ChatResponseChunk();
        var text = new StringBuilder();
        var toolCalls = new List<JsonElement>();

        if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in output.EnumerateArray())
            {
                if (IsFunctionCall(item))
                {
                    toolCalls.Add(ToChatToolCall(item, index++));
                    continue;
                }

                if (!item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in content.EnumerateArray())
                {
                    if (part.TryGetProperty("type", out var partType)
                        && partType.ValueKind == JsonValueKind.String
                        && partType.GetString() == "output_text"
                        && part.TryGetProperty("text", out var partText)
                        && partText.ValueKind == JsonValueKind.String)
                    {
                        text.Append(partText.GetString());
                    }
                }
            }
        }

        if (text.Length > 0) chunk.ContentDelta = text.ToString();
        if (toolCalls.Count > 0) chunk.ToolCalls = toolCalls;
        chunk.FinishReason = FinishReasonFor(root);
        ApplyUsage(root, chunk);
        return chunk;
    }

    private static bool IsFunctionCall(JsonElement item) =>
        item.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
        && type.GetString() == "function_call";

    /// <summary>Rebuilds a Responses <c>function_call</c> item as a chat-completions tool call.</summary>
    private static JsonElement ToChatToolCall(JsonElement item, int index)
    {
        var callId = item.TryGetProperty("call_id", out var call) && call.ValueKind == JsonValueKind.String
            ? call.GetString()
            : item.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                ? id.GetString()
                : null;

        var name = item.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : "";

        var arguments = item.TryGetProperty("arguments", out var argsElement) && argsElement.ValueKind == JsonValueKind.String
            ? argsElement.GetString()
            : "{}";

        var node = new JsonObject
        {
            ["index"] = index,
            ["id"] = callId ?? "",
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = arguments
            }
        };

        return JsonSerializer.SerializeToElement(node);
    }

    private static string FinishReasonFor(JsonElement response)
    {
        if (response.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (IsFunctionCall(item))
                {
                    return "tool_calls";
                }
            }
        }

        return response.TryGetProperty("status", out var status)
               && status.ValueKind == JsonValueKind.String
               && status.GetString() == "incomplete"
            ? "length"
            : "stop";
    }

    private static void ApplyUsage(JsonElement response, ChatResponseChunk chunk)
    {
        if (!response.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (usage.TryGetProperty("input_tokens", out var input) && input.TryGetInt32(out var inputTokens))
        {
            chunk.PromptTokens = inputTokens;
        }
        if (usage.TryGetProperty("output_tokens", out var output) && output.TryGetInt32(out var outputTokens))
        {
            chunk.CompletionTokens = outputTokens;
        }
    }
}
