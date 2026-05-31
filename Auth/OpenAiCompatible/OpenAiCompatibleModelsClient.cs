using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Auth.OpenAiCompatible;

/// <summary>
/// Lists models from any OpenAI-compatible <c>/models</c> endpoint (OpenAI, OpenRouter,
/// Groq, DeepSeek, ...). The response shape is <c>{ "data": [ { "id": ... } ] }</c>; some
/// providers also include a <c>context_length</c>, which we surface when present.
/// </summary>
public sealed class OpenAiCompatibleModelsClient
{
    private readonly HttpClient _http;

    public OpenAiCompatibleModelsClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<IReadOnlyList<ModelEntry>> ListAsync(string baseUrl, string apiKey, CancellationToken cancellationToken)
    {
        var url = baseUrl.TrimEnd('/') + "/models";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Headers.Accept.Add(new("application/json"));

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var dto = await response.Content.ReadFromJsonAsync<ModelsResponse>(cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Empty models response.");

        return (dto.Data ?? new List<ModelEntry>())
            .Where(m => !string.IsNullOrEmpty(m.Id))
            .OrderBy(m => m.Id, StringComparer.Ordinal)
            .ToList();
    }

    public sealed record ModelEntry(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("context_length")] int? ContextLength);

    private sealed record ModelsResponse(
        [property: JsonPropertyName("data")] List<ModelEntry>? Data);
}
