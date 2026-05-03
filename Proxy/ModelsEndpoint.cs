using AiProxy.Auth;
using AiProxy.Auth.Copilot;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AiProxy.Proxy;

public static class ModelsEndpoint
{
    public static async Task<IResult> HandleAsync(IEnumerable<IAuthProvider> providers, CancellationToken cancellationToken)
    {
        var copilot = providers.FirstOrDefault(p => p.Name == CopilotAuthProvider.ProviderName);
        if (copilot is null)
        {
            return Results.Json(new
            {
                error = new { message = "Copilot provider not registered.", type = "server_error", code = 500 }
            }, statusCode: StatusCodes.Status500InternalServerError);
        }

        var selected = await copilot.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);
        if (selected.Count == 0)
        {
            return Results.Json(new
            {
                error = new
                {
                    message = "No models available. Run 'AiProxy connect copilot' first.",
                    type = "service_unavailable",
                    code = 503
                }
            }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var data = selected.Select(id => new
        {
            id,
            @object = "model",
            created,
            owned_by = "github-copilot"
        });

        return Results.Json(new { @object = "list", data });
    }
}
