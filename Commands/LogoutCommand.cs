using AiProxy.Auth;
using Microsoft.Extensions.DependencyInjection;

namespace AiProxy.Commands;

public static class LogoutCommand
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
            var removed = await provider.LogoutAsync(cancellationToken).ConfigureAwait(false);
            if (removed)
            {
                Console.WriteLine($"Logged out of '{provider.Name}'. Stored auth state removed.");
            }
            else
            {
                Console.WriteLine($"No stored auth state for '{provider.Name}'. Nothing to do.");
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Logout failed: {ex.Message}");
            return 1;
        }
    }
}
