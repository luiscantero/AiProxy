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

        // Filter: must be reachable over /chat/completions or /responses, chat-capable,
        // picker-enabled and streaming. Defensive: missing fields default to "include".
        // Returns null when the model is usable, otherwise a human-readable reason so
        // 'AiProxy models copilot' can explain omissions.
        static string? RejectionReason(ModelEntry m)
        {
            if (string.IsNullOrEmpty(m.Id)) return "missing id";
            if (m.ModelPickerEnabled == false) return "model_picker_enabled=false";

            // The Copilot 'supported_endpoints' field is the most reliable signal. Models like
            // gpt-5.5 and the gpt-5.6-* family are 'chat'-typed but serve only /responses, so
            // they are reachable - just over a different wire format.
            if (m.SupportedEndpoints is { Count: > 0 } eps
                && !HasHttpEndpoint(eps, "/chat/completions")
                && !HasHttpEndpoint(eps, "/responses"))
            {
                return $"no /chat/completions or /responses endpoint (supports: {string.Join(", ", eps)})";
            }

            if (m.Capabilities is { } caps)
            {
                if (!string.IsNullOrEmpty(caps.Type) && !string.Equals(caps.Type, "chat", StringComparison.OrdinalIgnoreCase))
                {
                    return $"capabilities.type={caps.Type}";
                }
                if (caps.Supports is { Streaming: false })
                {
                    return "streaming not supported";
                }
            }
            return null;
        }

        var usable = new List<ModelEntry>();
        var excluded = new List<ExcludedModel>();
        foreach (var entry in entries)
        {
            var reason = RejectionReason(entry);
            if (reason is null)
            {
                usable.Add(entry);
            }
            else
            {
                excluded.Add(new ExcludedModel(entry.Id ?? "(no id)", reason));
            }
        }

        usable.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));
        excluded.Sort((a, b) => StringComparer.Ordinal.Compare(a.Id, b.Id));

        return new ModelsResult(usable, excluded);
    }

    public sealed record ModelsResult(IReadOnlyList<ModelEntry> Models, IReadOnlyList<ExcludedModel> Excluded);

    public sealed record ExcludedModel(string Id, string Reason);

    /// <summary>
    /// True when the model serves only the Responses API, so the proxy must translate the
    /// request instead of posting it to /chat/completions.
    /// </summary>
    public static bool UsesResponsesApi(ModelEntry model) =>
        model.SupportedEndpoints is { Count: > 0 } endpoints
        && !HasHttpEndpoint(endpoints, "/chat/completions")
        && HasHttpEndpoint(endpoints, "/responses");

    /// <summary>
    /// Matches an HTTP endpoint path, ignoring the parallel WebSocket entries Copilot also
    /// advertises ("ws:/responses"), which this proxy does not speak.
    /// </summary>
    private static bool HasHttpEndpoint(IReadOnlyList<string> endpoints, string path) =>
        endpoints.Any(e => !e.StartsWith("ws:", StringComparison.OrdinalIgnoreCase)
                           && e.EndsWith(path, StringComparison.OrdinalIgnoreCase));

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
