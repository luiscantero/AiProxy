using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace AiProxy.Auth.Copilot;

/// <summary>
/// Lists models exposed by the Copilot Chat API.
/// </summary>
public sealed class CopilotModelsClient
{
    private readonly HttpClient _http;
    private readonly IOptions<AiProxyOptions> _options;

    public CopilotModelsClient(HttpClient http, IOptions<AiProxyOptions> options)
    {
        _http = http;
        _options = options;
    }

    public async Task<ModelsResult> ListAsync(string copilotBearer, string? apiBaseUrl, CancellationToken cancellationToken)
    {
        var baseUrl = !string.IsNullOrEmpty(apiBaseUrl)
            ? apiBaseUrl
            : _options.Value.Copilot.UpstreamBaseUrl;
        var url = baseUrl.TrimEnd('/') + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", copilotBearer);
        request.Headers.Accept.Add(new("application/json"));
        CopilotHeaders.Apply(request);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<ModelsResponse>(cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Empty models response.");

        var entries = dto.Data ?? new List<ModelEntry>();

        // Filter: must support /chat/completions, be chat-capable, picker-enabled, support streaming.
        // Defensive: missing fields default to "include".
        bool IsUsable(ModelEntry m)
        {
            if (string.IsNullOrEmpty(m.Id)) return false;
            if (m.ModelPickerEnabled == false) return false;

            // The Copilot 'supported_endpoints' field is the most reliable signal.
            // Models like gpt-5.5 are 'chat'-typed but only support /responses, not
            // /chat/completions, so they 400 if forwarded to chat completions.
            if (m.SupportedEndpoints is { Count: > 0 } eps
                && !eps.Any(e => e.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (m.Capabilities is { } caps)
            {
                if (!string.IsNullOrEmpty(caps.Type) && !string.Equals(caps.Type, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
                if (caps.Supports is { Streaming: false })
                {
                    return false;
                }
            }
            return true;
        }

        var usable = entries
            .Where(IsUsable)
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        return new ModelsResult(usable);
    }

    public sealed record ModelsResult(IReadOnlyList<ModelEntry> Models);

    public sealed record ModelEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("vendor")] string? Vendor,
        [property: JsonPropertyName("version")] string? Version,
        [property: JsonPropertyName("model_picker_enabled")] bool? ModelPickerEnabled,
        [property: JsonPropertyName("supported_endpoints")] List<string>? SupportedEndpoints,
        [property: JsonPropertyName("capabilities")] CapabilitiesEntry? Capabilities);

    public sealed record CapabilitiesEntry(
        [property: JsonPropertyName("type")] string? Type,
        [property: JsonPropertyName("family")] string? Family,
        [property: JsonPropertyName("limits")] LimitsEntry? Limits,
        [property: JsonPropertyName("supports")] SupportsEntry? Supports);

    public sealed record LimitsEntry(
        [property: JsonPropertyName("max_prompt_tokens")] int? MaxPromptTokens,
        [property: JsonPropertyName("max_output_tokens")] int? MaxOutputTokens,
        [property: JsonPropertyName("max_context_window_tokens")] int? MaxContextWindowTokens);

    public sealed record SupportsEntry(
        [property: JsonPropertyName("streaming")] bool? Streaming,
        [property: JsonPropertyName("tool_calls")] bool? ToolCalls,
        [property: JsonPropertyName("parallel_tool_calls")] bool? ParallelToolCalls,
        [property: JsonPropertyName("vision")] bool? Vision);

    private sealed record ModelsResponse(
        [property: JsonPropertyName("data")] List<ModelEntry>? Data);
}
