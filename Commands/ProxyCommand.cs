using AiProxy.Auth;
using AiProxy.Auth.Copilot;
using AiProxy.Auth.OpenAiCompatible;
using AiProxy.Pipeline;
using AiProxy.Pipeline.Middlewares;
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

        // Warn if no auth state, listing models per connected provider.
        var byProvider = await ProviderResolver.ListAllAsync(
            app.Services.GetServices<IAuthProvider>(), cancellationToken).ConfigureAwait(false);
        if (byProvider.Count == 0)
        {
            Console.WriteLine("  WARNING      : No connected providers. Run 'AiProxy connect <provider>' first.");
        }
        else
        {
            foreach (var (provider, models) in byProvider)
            {
                Console.WriteLine($"  {provider.Name,-12} : {string.Join(", ", models)}");
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
        services.AddHttpClient<OpenAiCompatibleModelsClient>();
        services.AddHttpClient("upstream");

        services.AddSingleton<CopilotAuthProvider>();
        services.AddSingleton<IAuthProvider>(sp => sp.GetRequiredService<CopilotAuthProvider>());

        // Register one OpenAI-compatible provider per configured upstream (OpenAI, OpenRouter,
        // Groq, DeepSeek, ...). This is the extension point: adding a provider is configuration
        // only — drop an entry under "OpenAiProviders" in appsettings and it lights up.
        var openAiProviders = configuration.GetSection(nameof(AiProxyOptions.OpenAiProviders))
            .Get<List<OpenAiCompatibleProviderOptions>>() ?? new List<OpenAiCompatibleProviderOptions>();
        foreach (var providerConfig in openAiProviders)
        {
            if (string.IsNullOrWhiteSpace(providerConfig.Name) || string.IsNullOrWhiteSpace(providerConfig.BaseUrl))
            {
                continue;
            }

            services.AddSingleton<IAuthProvider>(sp => new OpenAiCompatibleAuthProvider(
                providerConfig,
                sp.GetRequiredService<ITokenStore>(),
                sp.GetRequiredService<OpenAiCompatibleModelsClient>(),
                sp.GetRequiredService<ILoggerFactory>().CreateLogger($"AiProxy.Provider.{providerConfig.Name}")));
        }

        // Chat pipeline: the terminal upstream invoker plus an ordered list of middlewares.
        // Add your own IChatMiddleware registrations here; they run in registration order
        // (outermost first), so a middleware can transform the request on the way to Copilot
        // and the response on the way back to the client (e.g. compress / decompress).
        services.AddSingleton<UpstreamChatInvoker>();

        // --- Chat middleware pipeline (registration order = execution order, outermost first) ---
        // Logging sits outermost so it observes the request as the client sent it and the final
        // response. The token-saving transforms then run inbound before the upstream call:
        //   CacheAligner  — stabilizes the system-prompt prefix so provider KV caches hit.
        //   JsonCrusher   — losslessly minifies embedded JSON (tool outputs, API/DB payloads).
        //   LogCompressor — squashes redundant log blocks (dupes + low-severity runs).
        //   Caveman       — LLM-driven natural-language compression (opt-in via Caveman.Enabled).
        //   ModelFallback — retries an unavailable model against prioritized alternatives (opt-in).
        services.AddSingleton<IChatMiddleware, LoggingChatMiddleware>();
        services.AddSingleton<IChatMiddleware, CacheAlignerMiddleware>();
        services.AddSingleton<IChatMiddleware, JsonCrusherMiddleware>();
        services.AddSingleton<IChatMiddleware, LogCompressorMiddleware>();

        services.AddSingleton<ICavemanTransformer, CavemanTransformer>();
        services.AddSingleton<IChatMiddleware, CavemanMiddleware>();

        // Innermost (closest to the upstream call): if the requested model is unavailable, retry
        // against a prioritized list of alternatives. Placed last so a fallback only re-sends the
        // already-transformed request — the outer prompt transforms run once. Opt-in via Fallback.Enabled.
        services.AddSingleton<IChatMiddleware, ModelFallbackMiddleware>();

        services.AddSingleton<ChatPipeline>();
    }
}
