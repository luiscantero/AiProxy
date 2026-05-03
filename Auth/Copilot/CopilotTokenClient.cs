using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace AiProxy.Auth.Copilot;

/// <summary>
/// Exchanges a GitHub OAuth token for a short-lived Copilot bearer token.
/// </summary>
public sealed class CopilotTokenClient
{
    private const string TokenUrl = "https://api.github.com/copilot_internal/v2/token";

    private readonly HttpClient _http;

    public CopilotTokenClient(HttpClient http)
    {
        _http = http;
        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new("application/json"));
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(CopilotHeaders.UserAgent))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CopilotHeaders.UserAgent);
        }
    }

    public async Task<CopilotToken> ExchangeAsync(string ghOAuthToken, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TokenUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", ghOAuthToken);
        request.Headers.TryAddWithoutValidation("Editor-Version", CopilotHeaders.EditorVersion);
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", CopilotHeaders.EditorPluginVersion);

        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Empty Copilot token response.");

        if (string.IsNullOrEmpty(dto.Token))
        {
            throw new InvalidOperationException("Copilot token response missing 'token'.");
        }

        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(dto.ExpiresAt);
        // The Copilot token response tells us which API host to use; this differs by plan
        // (individual / business / enterprise). Hitting the wrong host yields 421.
        var apiBaseUrl = dto.Endpoints?.Api;
        return new CopilotToken(dto.Token, expiresAt, apiBaseUrl);
    }

    private sealed record TokenResponse(
        [property: JsonPropertyName("token")] string Token,
        [property: JsonPropertyName("expires_at")] long ExpiresAt,
        [property: JsonPropertyName("endpoints")] EndpointsDto? Endpoints);

    private sealed record EndpointsDto(
        [property: JsonPropertyName("api")] string? Api,
        [property: JsonPropertyName("proxy")] string? Proxy,
        [property: JsonPropertyName("telemetry")] string? Telemetry);
}

public sealed record CopilotToken(string Token, DateTimeOffset ExpiresAt, string? ApiBaseUrl);
