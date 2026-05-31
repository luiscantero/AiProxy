using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace AiProxy.Pipeline;

/// <summary>
/// The terminal stage of the chat pipeline: it sends the (possibly middleware-mutated)
/// OpenAI-shaped request to GitHub Copilot and exposes the response as a normalized stream
/// of <see cref="ChatResponseChunk"/>. All surface- and middleware-specific concerns live
/// outside this class; here we only speak the upstream's OpenAI wire protocol.
/// </summary>
public sealed class UpstreamChatInvoker
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<AiProxyOptions> _options;

    public UpstreamChatInvoker(IHttpClientFactory httpClientFactory, IOptions<AiProxyOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
    }

    public async Task InvokeAsync(ChatPipelineContext context)
    {
        var cancellationToken = context.CancellationToken;

        var baseUrl = await context.Provider.GetUpstreamApiBaseUrlAsync(cancellationToken).ConfigureAwait(false)
                      ?? _options.Value.Copilot.UpstreamBaseUrl;
        var url = baseUrl.TrimEnd('/') + "/chat/completions";

        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(context.UpstreamRequest, JsonOptions);

        var http = _httpClientFactory.CreateClient("upstream");
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(
            context.IsStreaming ? "text/event-stream" : "application/json"));

        // The provider owns authentication and any provider-specific headers, so this terminal
        // stage stays wire-format-only and never needs to change when a new provider is added.
        await context.Provider.PrepareUpstreamRequestAsync(request, cancellationToken).ConfigureAwait(false);

        var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            response.Dispose();
            throw new UpstreamException(status, errorBody);
        }

        context.ResponseChunks = context.IsStreaming
            ? ReadStreamingChunksAsync(response, cancellationToken)
            : ReadNonStreamingChunkAsync(response, cancellationToken);
    }

    /// <summary>
    /// Parses the upstream Server-Sent Events stream into normalized chunks. The
    /// <paramref name="response"/> is owned by this iterator and disposed when enumeration ends.
    /// </summary>
    private static async IAsyncEnumerable<ChatResponseChunk> ReadStreamingChunksAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            string? line;
            while ((line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false)) != null)
            {
                if (line.Length == 0) continue;
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line.AsSpan("data:".Length).Trim().ToString();
                if (payload == "[DONE]") break;
                if (payload.Length == 0) continue;

                ChatResponseChunk? chunk;
                try
                {
                    chunk = ParseChunk(payload);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (chunk is not null)
                {
                    yield return chunk;
                }
            }
        }
    }

    private static async IAsyncEnumerable<ChatResponseChunk> ReadNonStreamingChunkAsync(
        HttpResponseMessage response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;

            var chunk = new ChatResponseChunk();

            if (root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0)
            {
                var first = choices[0];
                if (first.TryGetProperty("message", out var msg))
                {
                    if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                    {
                        chunk.ContentDelta = content.GetString();
                    }
                    if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                    {
                        chunk.ToolCalls = toolCalls.EnumerateArray().Select(e => e.Clone()).ToList();
                    }
                }
                if (first.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                {
                    chunk.FinishReason = fr.GetString();
                }
            }

            ApplyUsage(root, chunk);

            yield return chunk;
        }
    }

    private static ChatResponseChunk? ParseChunk(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var root = doc.RootElement;

        var chunk = new ChatResponseChunk();
        ApplyUsage(root, chunk);

        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];

            if (choice.TryGetProperty("delta", out var delta) && delta.ValueKind == JsonValueKind.Object)
            {
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    chunk.ContentDelta = content.GetString();
                }
                if (delta.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.ValueKind == JsonValueKind.Array)
                {
                    chunk.ToolCalls = toolCalls.EnumerateArray().Select(e => e.Clone()).ToList();
                }
            }

            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
            {
                chunk.FinishReason = fr.GetString();
            }
        }

        // Drop frames that carry nothing actionable (e.g. role-only deltas) unless they
        // report usage or a finish reason, which downstream surfaces still need.
        if (chunk.ContentDelta is null
            && chunk.ToolCalls is null
            && chunk.FinishReason is null
            && chunk.PromptTokens is null
            && chunk.CompletionTokens is null)
        {
            return null;
        }

        return chunk;
    }

    private static void ApplyUsage(JsonElement root, ChatResponseChunk chunk)
    {
        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pti))
            {
                chunk.PromptTokens = pti;
            }
            if (usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cti))
            {
                chunk.CompletionTokens = cti;
            }
        }
    }
}
