using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiProxy.Auth;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Pipeline.Middlewares;

/// <summary>The direction of a caveman transform.</summary>
public enum CavemanDirection
{
    /// <summary>Fluent prose → caveman (token-reducing) form.</summary>
    Compress,

    /// <summary>Caveman form → fluent prose.</summary>
    Decompress,
}

/// <summary>
/// Performs a caveman compression/decompression of a single block of text by delegating to an
/// LLM. Implementations are expected to be fail-open: they return <c>null</c> when the transform
/// cannot be performed so callers can fall back to the original text.
/// </summary>
public interface ICavemanTransformer
{
    Task<string?> TransformAsync(
        string text,
        CavemanDirection direction,
        ILogger logger,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default <see cref="ICavemanTransformer"/>. It resolves the configured caveman provider (e.g. a
/// local Ollama exposed via its OpenAI-compatible endpoint), then issues a non-streaming
/// <c>/chat/completions</c> call whose system prompt encodes the caveman rules. Because the
/// transform is just another OpenAI-shaped chat call, any registered provider can drive it.
/// </summary>
public sealed class CavemanTransformer : ICavemanTransformer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IEnumerable<IAuthProvider> _providers;
    private readonly IOptions<AiProxyOptions> _options;

    public CavemanTransformer(
        IHttpClientFactory httpClientFactory,
        IEnumerable<IAuthProvider> providers,
        IOptions<AiProxyOptions> options)
    {
        _httpClientFactory = httpClientFactory;
        _providers = providers;
        _options = options;
    }

    public async Task<string?> TransformAsync(
        string text,
        CavemanDirection direction,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var cfg = _options.Value.Caveman;

        var provider = _providers.FirstOrDefault(
            p => p.Name.Equals(cfg.Provider, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            logger.LogWarning(
                "Caveman: configured provider '{Provider}' is not registered; skipping transform.",
                cfg.Provider);
            return null;
        }

        if (string.IsNullOrWhiteSpace(cfg.Model))
        {
            logger.LogWarning("Caveman: no model configured; skipping transform.");
            return null;
        }

        try
        {
            var baseUrl = await provider.GetUpstreamApiBaseUrlAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                logger.LogWarning(
                    "Caveman: provider '{Provider}' has no base URL; run 'AiProxy connect {Provider}'.",
                    cfg.Provider, cfg.Provider);
                return null;
            }

            var url = baseUrl.TrimEnd('/') + "/chat/completions";

            var body = new JsonObject
            {
                ["model"] = cfg.Model,
                ["stream"] = false,
                ["temperature"] = 0,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = SystemPromptFor(direction) },
                    new JsonObject { ["role"] = "user", ["content"] = text },
                },
            };

            var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(body, JsonOptions);

            var http = _httpClientFactory.CreateClient("upstream");
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new ByteArrayContent(bodyBytes),
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            await provider.PrepareUpstreamRequestAsync(request, cancellationToken).ConfigureAwait(false);

            using var response = await http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                logger.LogDebug(
                    "Caveman: transform call failed ({Status}); using original text. Body: {Body}",
                    (int)response.StatusCode, error);
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

            var content = ExtractContent(doc.RootElement);
            if (string.IsNullOrWhiteSpace(content))
            {
                logger.LogDebug("Caveman: transform returned empty content; using original text.");
                return null;
            }

            return content.Trim();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Caveman: transform threw; using original text.");
            return null;
        }
    }

    private static string? ExtractContent(JsonElement root)
    {
        if (root.TryGetProperty("choices", out var choices)
            && choices.ValueKind == JsonValueKind.Array
            && choices.GetArrayLength() > 0)
        {
            var first = choices[0];
            if (first.TryGetProperty("message", out var msg)
                && msg.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString();
            }
        }

        return null;
    }

    private static string SystemPromptFor(CavemanDirection direction) =>
        direction == CavemanDirection.Compress ? CompressPrompt : DecompressPrompt;

    private const string CompressPrompt =
        """
        You are a lossless semantic compressor ("caveman compression"). Rewrite the user's text so an
        LLM can still reconstruct every fact, while using far fewer tokens. Remove only what is
        predictable; keep everything unpredictable.

        Rules:
        - One atomic thought per sentence. Aim for 2-5 words per sentence.
        - Drop articles (a, an, the), auxiliaries (is, are, was, be, have), and pure intensifiers
          (very, quite, really, extremely).
        - Remove connectives (because, since, however, therefore, so that). Express cause/effect as
          adjacent sentences instead.
        - Use active voice, present tense.
        - NEVER drop or alter numbers, names, dates, units, code, technical terms, constraints, or
          negations (not, no, never, without). Keep uncertainty qualifiers (might, seems, approximately).
        - Never invent facts that are not in the original.
        - Keep every reasoning step explicit; the reader must reconstruct the full logic chain.

        Output ONLY the compressed text. No preamble, no explanation, no code fences.
        """;

    private const string DecompressPrompt =
        """
        You expand caveman-compressed text back into fluent, natural English. The input uses
        clipped, telegraphic sentences with grammar stripped out.

        Rules:
        - Restore articles, auxiliaries, and connectives so the prose reads naturally.
        - Use active voice and present tense unless the content clearly implies otherwise.
        - Preserve every fact, number, name, date, unit, technical term, constraint, and negation
          exactly as given. Do not add new facts.

        Output ONLY the expanded text. No preamble, no explanation, no code fences.
        """;
}
