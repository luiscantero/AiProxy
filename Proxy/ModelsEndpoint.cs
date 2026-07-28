using AiProxy.Auth;
using AiProxy.Pipeline;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace AiProxy.Proxy;

public static class ModelsEndpoint
{
    public static async Task<IResult> HandleAsync(
        IEnumerable<IAuthProvider> providers,
        IOptions<AiProxyOptions> options,
        CancellationToken cancellationToken)
    {
        var byProvider = await ProviderResolver.ListAllAsync(providers, cancellationToken).ConfigureAwait(false);
        if (byProvider.Count == 0)
        {
            return Results.Json(new
            {
                error = new
                {
                    message = "No models available. Run 'AiProxy connect <provider>' first.",
                    type = "service_unavailable",
                    code = 503
                }
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var efforts = options.Value.ReasoningEffort;
        var data = new List<object>();
        foreach (var (provider, ids) in byProvider)
        {
            var infos = await provider.GetModelInfosAsync(cancellationToken).ConfigureAwait(false);
            foreach (var id in ids)
            {
                infos.TryGetValue(id, out var info);
                foreach (var publishedId in ReasoningEffort.Expand(id, info, efforts))
                {
                    data.Add(new
                    {
                        id = publishedId,
                        @object = "model",
                        created,
                        owned_by = provider.Name
                    });
                }
            }
        }

        return Results.Json(new { @object = "list", data });
    }
}

