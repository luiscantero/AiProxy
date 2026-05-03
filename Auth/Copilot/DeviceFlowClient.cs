using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Auth.Copilot;

/// <summary>
/// Implements the GitHub OAuth Device Flow for Copilot.
/// </summary>
public sealed class DeviceFlowClient
{
    private const string DeviceCodeUrl = "https://github.com/login/device/code";
    private const string AccessTokenUrl = "https://github.com/login/oauth/access_token";

    private readonly HttpClient _http;
    private readonly IOptions<AiProxyOptions> _options;
    private readonly ILogger<DeviceFlowClient> _logger;

    public DeviceFlowClient(HttpClient http, IOptions<AiProxyOptions> options, ILogger<DeviceFlowClient> logger)
    {
        _http = http;
        _options = options;
        _logger = logger;

        _http.DefaultRequestHeaders.Accept.Clear();
        _http.DefaultRequestHeaders.Accept.Add(new("application/json"));
        if (!_http.DefaultRequestHeaders.UserAgent.TryParseAdd(CopilotHeaders.UserAgent))
        {
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", CopilotHeaders.UserAgent);
        }
    }

    public async Task<DeviceCodeResponse> RequestDeviceCodeAsync(CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = _options.Value.Copilot.ClientId,
            ["scope"] = "read:user"
        });

        using var response = await _http.PostAsync(DeviceCodeUrl, content, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<DeviceCodeResponse>(cancellationToken).ConfigureAwait(false)
                  ?? throw new InvalidOperationException("Empty device-code response.");
        return dto;
    }

    /// <summary>
    /// Polls the access-token endpoint until the user authorizes, expires, or the operation is cancelled.
    /// </summary>
    public async Task<string> PollForAccessTokenAsync(DeviceCodeResponse deviceCode, CancellationToken cancellationToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, deviceCode.Interval));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _options.Value.Copilot.ClientId,
                ["device_code"] = deviceCode.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            using var response = await _http.PostAsync(AccessTokenUrl, content, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var dto = await response.Content.ReadFromJsonAsync<AccessTokenResponse>(cancellationToken).ConfigureAwait(false)
                      ?? throw new InvalidOperationException("Empty token response.");

            if (!string.IsNullOrEmpty(dto.AccessToken))
            {
                return dto.AccessToken;
            }

            switch (dto.Error)
            {
                case "authorization_pending":
                    break;
                case "slow_down":
                    interval += TimeSpan.FromSeconds(5);
                    _logger.LogDebug("Device-flow slow_down received; new interval {Interval}s", interval.TotalSeconds);
                    break;
                case "expired_token":
                case "access_denied":
                    throw new InvalidOperationException($"Device flow failed: {dto.Error} ({dto.ErrorDescription})");
                default:
                    throw new InvalidOperationException($"Unexpected device-flow error: {dto.Error} ({dto.ErrorDescription})");
            }
        }

        throw new TimeoutException("Device flow expired before user authorized.");
    }

    public sealed record DeviceCodeResponse(
        [property: JsonPropertyName("device_code")] string DeviceCode,
        [property: JsonPropertyName("user_code")] string UserCode,
        [property: JsonPropertyName("verification_uri")] string VerificationUri,
        [property: JsonPropertyName("expires_in")] int ExpiresIn,
        [property: JsonPropertyName("interval")] int Interval);

    private sealed record AccessTokenResponse(
        [property: JsonPropertyName("access_token")] string? AccessToken,
        [property: JsonPropertyName("token_type")] string? TokenType,
        [property: JsonPropertyName("scope")] string? Scope,
        [property: JsonPropertyName("error")] string? Error,
        [property: JsonPropertyName("error_description")] string? ErrorDescription);
}
