using AiProxy.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Commands;

public static class ModelsCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, string[] args, CancellationToken cancellationToken)
    {
        var providerName = args.Length > 1 ? args[1] : "copilot";

        var provider = services
            .GetServices<IAuthProvider>()
            .FirstOrDefault(p => string.Equals(p.Name, providerName, StringComparison.OrdinalIgnoreCase));

        if (provider is null)
        {
            Console.Error.WriteLine($"Unknown auth provider: '{providerName}'.");
            Console.Error.WriteLine("Available providers:");
            foreach (var p in services.GetServices<IAuthProvider>())
            {
                Console.Error.WriteLine($"  - {p.Name}");
            }
            return 2;
        }

        try
        {
            await provider.RunSelectModelsAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Select models failed: {ex.Message}");
            return 1;
        }
    }
}
