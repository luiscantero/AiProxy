using AiProxy.Auth;
using Microsoft.AspNetCore.Http;

namespace AiProxy.Proxy;

public static class ModelsEndpoint
{
    public static async Task<IResult> HandleAsync(IEnumerable<IAuthProvider> providers, CancellationToken cancellationToken)
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
        var data = byProvider.SelectMany(pm => pm.Models.Select(id => new
        {
            id,
            @object = "model",
            created,
            owned_by = pm.Provider.Name
        }));

        return Results.Json(new { @object = "list", data });
    }
}

