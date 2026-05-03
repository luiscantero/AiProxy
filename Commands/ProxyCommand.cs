using AiProxy.Auth;
using AiProxy.Auth.Copilot;
using AiProxy.Proxy;
using AiProxy.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Commands;

public static class ProxyCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory
        });

        ServiceRegistration.Configure(builder.Services, builder.Configuration);

        var listenUrl = builder.Configuration["ListenUrl"] ?? "http://127.0.0.1:11435";
        builder.WebHost.UseUrls(listenUrl);

        var app = builder.Build();

        // Auth gate must run before any of our routed endpoints.
        app.UseMiddleware<ApiKeyAuthMiddleware>();

        var optionsAtBuild = app.Services.GetRequiredService<IOptions<AiProxyOptions>>().Value;

        if (optionsAtBuild.Apis.OpenAi)
        {
            app.MapGet("/v1/models", ModelsEndpoint.HandleAsync);
            app.MapPost("/v1/chat/completions", ChatCompletionsEndpoint.HandleAsync);
        }

        if (optionsAtBuild.Apis.Ollama)
        {
            // Ollama-shaped surface for VS Code's built-in Ollama provider.
            app.MapGet("/api/version", OllamaEndpoints.Version);
            app.MapGet("/api/tags", OllamaEndpoints.Tags);
            app.MapPost("/api/show", OllamaEndpoints.Show);
            app.MapPost("/api/chat", OllamaEndpoints.Chat);
        }

        var options = optionsAtBuild;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("AiProxy");

        Console.WriteLine();
        Console.WriteLine("== AiProxy ==");
        Console.WriteLine($"  Listening on : {options.ListenUrl}");
        Console.WriteLine();

        if (options.Apis.Ollama)
        {
            Console.WriteLine("  VS Code BYOK : Use the 'Ollama' provider.");
            Console.WriteLine($"                 Base URL = {options.ListenUrl.TrimEnd('/')}");
            Console.WriteLine("                 (No API key required for the Ollama routes.)");
            Console.WriteLine();
        }

        if (options.Apis.OpenAi)
        {
            Console.WriteLine("  OpenAI-shape : (alternative for curl/scripts)");
            Console.WriteLine($"                 Base URL = {options.ListenUrl.TrimEnd('/')}/v1");
        }

        if (!options.Apis.Ollama && !options.Apis.OpenAi)
        {
            Console.WriteLine("  WARNING      : Both Apis.Ollama and Apis.OpenAi are disabled. No routes are exposed.");
        }

        // Warn if no auth state.
        var copilot = app.Services.GetServices<IAuthProvider>().FirstOrDefault(p => p.Name == CopilotAuthProvider.ProviderName);
        if (copilot is not null)
        {
            var models = await copilot.GetSelectedModelsAsync(cancellationToken).ConfigureAwait(false);
            if (models.Count == 0)
            {
                Console.WriteLine("  WARNING      : No Copilot auth state found. Run 'AiProxy connect copilot' first.");
            }
            else
            {
                Console.WriteLine($"  Models       : {string.Join(", ", models)}");
            }
        }
        Console.WriteLine();

        try
        {
            await app.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }
}

internal static class ServiceRegistration
{
    public static void Configure(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiProxyOptions>().Bind(configuration);

        services.AddSingleton<ITokenStore, DpapiTokenStore>();

        services.AddHttpClient<DeviceFlowClient>();
        services.AddHttpClient<CopilotTokenClient>();
        services.AddHttpClient<CopilotModelsClient>();
        services.AddHttpClient("upstream");

        services.AddSingleton<CopilotAuthProvider>();
        services.AddSingleton<IAuthProvider>(sp => sp.GetRequiredService<CopilotAuthProvider>());
    }
}
