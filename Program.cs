using AiProxy;
using AiProxy.Commands;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    if (!cts.IsCancellationRequested)
    {
        e.Cancel = true;
        cts.Cancel();
    }
};

if (args.Length == 0)
{
    return await ProxyCommand.RunAsync(args, cts.Token).ConfigureAwait(false);
}

switch (args[0].ToLowerInvariant())
{
    case "connect":
    {
        // Build a lightweight host (no Kestrel) just for the connect workflow.
        var builder = Host.CreateApplicationBuilder(args);
        ServiceRegistration.Configure(builder.Services, builder.Configuration);
        // Quieten ASP.NET-style logs in console mode.
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        using var host = builder.Build();
        return await ConnectCommand.RunAsync(host.Services, args, cts.Token).ConfigureAwait(false);
    }

    case "logout":
    {
        var builder = Host.CreateApplicationBuilder(args);
        ServiceRegistration.Configure(builder.Services, builder.Configuration);
        builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
        using var host = builder.Build();
        return await LogoutCommand.RunAsync(host.Services, args, cts.Token).ConfigureAwait(false);
    }

    case "--help":
    case "-h":
    case "help":
        PrintUsage();
        return 0;

    default:
        Console.Error.WriteLine($"Unknown command: '{args[0]}'.");
        Console.Error.WriteLine();
        PrintUsage();
        return 2;
}

static void PrintUsage()
{
    Console.WriteLine("AiProxy - local OpenRouter-shaped proxy to bring-your-own AI models into VS Code.");
    Console.WriteLine();
    Console.WriteLine("Usage:");
    Console.WriteLine("  AiProxy                       Start proxy mode (default).");
    Console.WriteLine("  AiProxy connect [provider]    Run a connect workflow (default provider: copilot).");
    Console.WriteLine("  AiProxy logout  [provider]    Remove stored auth state (default provider: copilot).");
    Console.WriteLine("  AiProxy help                  Show this message.");
    Console.WriteLine();
    Console.WriteLine("Configuration is read from appsettings.json (ListenUrl, ProxyApiKey, Copilot:*).");
}
