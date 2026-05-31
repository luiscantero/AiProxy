using System.Text.Json.Nodes;
using AiProxy.Auth;
using AiProxy.Pipeline;
using AiProxy.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiProxy.Tests;

/// <summary>
/// Shared helpers for exercising <see cref="IChatMiddleware"/> implementations in isolation.
/// </summary>
internal static class TestPipeline
{
    /// <summary>
    /// Builds a minimal <see cref="ChatPipelineContext"/> around the given OpenAI-shaped request.
    /// </summary>
    public static ChatPipelineContext CreateContext(JsonObject upstreamRequest) => new()
    {
        Http = new DefaultHttpContext(),
        Surface = ClientSurface.OpenAi,
        Model = "gpt-4o",
        IsStreaming = false,
        UpstreamRequest = upstreamRequest,
        Provider = new FakeAuthProvider(),
        Logger = NullLogger.Instance,
    };

    /// <summary>
    /// Runs a single middleware end-to-end with a no-op terminal and returns the (possibly
    /// mutated) request so assertions can inspect it.
    /// </summary>
    public static async Task<JsonObject> RunAsync(IChatMiddleware middleware, JsonObject upstreamRequest)
    {
        var context = CreateContext(upstreamRequest);
        var terminalReached = false;
        await middleware.InvokeAsync(context, _ =>
        {
            terminalReached = true;
            return Task.CompletedTask;
        });

        Assert.True(terminalReached, "Middleware must call next(context) so the pipeline continues.");
        return context.UpstreamRequest;
    }

    /// <summary>Builds an OpenAI-shaped request with a single message of the given role/content.</summary>
    public static JsonObject Request(string role, string content) => new()
    {
        ["model"] = "gpt-4o",
        ["messages"] = new JsonArray
        {
            new JsonObject { ["role"] = role, ["content"] = content },
        },
    };

    /// <summary>Reads the string content of the message at the given index.</summary>
    public static string? Content(JsonObject request, int messageIndex = 0)
    {
        var messages = request["messages"] as JsonArray;
        var msg = messages?[messageIndex] as JsonObject;
        return msg?["content"]?.GetValue<string>();
    }
}

/// <summary>No-op auth provider so a pipeline context can be constructed without real credentials.</summary>
internal sealed class FakeAuthProvider : IAuthProvider
{
    public string Name => "fake";

    public Task RunConnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task RunSelectModelsAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task<bool> LogoutAsync(CancellationToken cancellationToken = default) => Task.FromResult(false);

    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult("test-token");

    public Task<IReadOnlyList<string>> GetSelectedModelsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<IReadOnlyDictionary<string, ModelInfo>> GetModelInfosAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, ModelInfo>>(new Dictionary<string, ModelInfo>());

    public Task<string?> GetUpstreamApiBaseUrlAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);
}
