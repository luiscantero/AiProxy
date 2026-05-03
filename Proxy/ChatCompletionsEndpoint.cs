using System.Net.Http.Headers;
using System.Text.Json;
using AiProxy.Auth;
using AiProxy.Auth.Copilot;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Proxy;

public static class ChatCompletionsEndpoint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task HandleAsync(
        HttpContext context,
        IEnumerable<IAuthProvider> providers,
        IHttpClientFactory httpClientFactory,
        IOptions<AiProxyOptions> options,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("AiProxy.Proxy.Chat");
        var cancellationToken = context.RequestAborted;

        var copilot = providers.FirstOrDefault(p => p.Name == CopilotAuthProvider.ProviderName);
        if (copilot is null)
        {
            await WriteErrorAsync(context, StatusCodes.Status500InternalServerError, "Copilot provider not registered.", "server_error");
            return;
        }

        // Read & parse body so we can validate the model and forward verbatim.
        using var ms = new MemoryStream();
        await context.Request.Body.CopyToAsync(ms, cancellationToken);
        var bodyBytes = ms.ToArray();

        string? requestedModel = null;
        bool isStream = false;
        try
        {
            using var doc = JsonDocument.Parse(bodyBytes);
            if (doc.RootElement.TryGetProperty("model", out var modelEl) && modelEl.ValueKind == JsonValueKind.String)
            {
                requestedModel = modelEl.GetString();
            }
            if (doc.RootElement.TryGetProperty("stream", out var streamEl) && streamEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                isStream = streamEl.GetBoolean();
            }
        }
        catch (JsonException ex)
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, $"Invalid JSON: {ex.Message}", "bad_request");
            return;
        }

        if (string.IsNullOrEmpty(requestedModel))
        {
            await WriteErrorAsync(context, StatusCodes.Status400BadRequest, "Missing 'model' in request.", "bad_request");
            return;
        }

        var allowedModels = await copilot.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);
        if (allowedModels.Count == 0)
        {
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable,
                "No models available. Run 'AiProxy connect copilot' first.", "service_unavailable");
            return;
        }
        if (!allowedModels.Contains(requestedModel))
        {
            await WriteErrorAsync(context, StatusCodes.Status404NotFound,
                $"Model '{requestedModel}' is not exposed by this proxy.", "model_not_found");
            return;
        }

        string upstreamBearer;
        try
        {
            upstreamBearer = await copilot.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to acquire upstream access token.");
            await WriteErrorAsync(context, StatusCodes.Status503ServiceUnavailable, ex.Message, "service_unavailable");
            return;
        }

        var upstreamBaseUrl = await copilot.GetUpstreamApiBaseUrlAsync(cancellationToken).ConfigureAwait(false)
                              ?? options.Value.Copilot.UpstreamBaseUrl;
        var upstreamUrl = upstreamBaseUrl.TrimEnd('/') + "/chat/completions";
        var http = httpClientFactory.CreateClient("upstream");

        using var upstreamRequest = new HttpRequestMessage(HttpMethod.Post, upstreamUrl)
        {
            Content = new ByteArrayContent(bodyBytes)
        };
        upstreamRequest.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", upstreamBearer);
        upstreamRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(isStream ? "text/event-stream" : "application/json"));
        CopilotHeaders.Apply(upstreamRequest);
        upstreamRequest.Headers.TryAddWithoutValidation("OpenAI-Intent", "conversation-panel");

        HttpResponseMessage upstreamResponse;
        try
        {
            upstreamResponse = await http.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Upstream request failed.");
            await WriteErrorAsync(context, StatusCodes.Status502BadGateway, $"Upstream error: {ex.Message}", "bad_gateway");
            return;
        }

        try
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;

            // Copy content headers (Content-Type especially), filter hop-by-hop.
            foreach (var header in upstreamResponse.Headers)
            {
                if (IsHopByHop(header.Key)) continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }
            foreach (var header in upstreamResponse.Content.Headers)
            {
                if (IsHopByHop(header.Key)) continue;
                if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                context.Response.Headers[header.Key] = header.Value.ToArray();
            }

            await using var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            if (isStream)
            {
                // Per-chunk flush for SSE.
                var buffer = new byte[8192];
                int read;
                while ((read = await upstreamStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    await context.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            else
            {
                await upstreamStream.CopyToAsync(context.Response.Body, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            upstreamResponse.Dispose();
        }
    }

    private static bool IsHopByHop(string headerName) =>
        headerName.Equals("Connection", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Keep-Alive", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Proxy-Authenticate", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("TE", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Trailer", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
        || headerName.Equals("Upgrade", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteErrorAsync(HttpContext context, int statusCode, string message, string type)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json";
        var json = JsonSerializer.Serialize(new
        {
            error = new { message, type, code = statusCode }
        }, JsonOptions);
        await context.Response.WriteAsync(json);
    }
}
