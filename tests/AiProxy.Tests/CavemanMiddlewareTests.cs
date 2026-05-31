using System.Text.Json.Nodes;
using AiProxy.Pipeline;
using AiProxy.Pipeline.Middlewares;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiProxy.Tests;

public class CavemanMiddlewareTests
{
    private static readonly string LongText =
        string.Join(' ', Enumerable.Repeat("the quick brown fox jumps over the lazy dog.", 20));

    private static CavemanMiddleware Create(FakeCavemanTransformer transformer, CavemanOptions options) =>
        new(transformer, Options.Create(new AiProxyOptions { Caveman = options }));

    private static CavemanOptions EnabledOptions() => new()
    {
        Enabled = true,
        Provider = "ollama",
        Model = "test-model",
        CompressRequests = true,
        DecompressResponses = false,
        Roles = new List<string> { "user" },
        MinCharacters = 10,
    };

    [Fact]
    public async Task Disabled_passes_request_through_untouched()
    {
        var transformer = new FakeCavemanTransformer();
        var options = EnabledOptions();
        options.Enabled = false;
        var middleware = Create(transformer, options);

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal(LongText, TestPipeline.Content(result));
        Assert.Equal(0, transformer.CompressCalls);
    }

    [Fact]
    public async Task Compresses_user_message_when_transform_is_shorter()
    {
        var transformer = new FakeCavemanTransformer { CompressResult = "fox jumps. dog lazy." };
        var middleware = Create(transformer, EnabledOptions());

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal("fox jumps. dog lazy.", TestPipeline.Content(result));
        Assert.Equal(1, transformer.CompressCalls);
    }

    [Fact]
    public async Task Skips_content_below_min_characters()
    {
        var transformer = new FakeCavemanTransformer { CompressResult = "x" };
        var options = EnabledOptions();
        options.MinCharacters = 1000;
        var middleware = Create(transformer, options);

        var request = TestPipeline.Request("user", "short message");
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal("short message", TestPipeline.Content(result));
        Assert.Equal(0, transformer.CompressCalls);
    }

    [Fact]
    public async Task Only_compresses_configured_roles()
    {
        var transformer = new FakeCavemanTransformer { CompressResult = "tiny" };
        var middleware = Create(transformer, EnabledOptions());

        var request = TestPipeline.Request("system", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal(LongText, TestPipeline.Content(result));
        Assert.Equal(0, transformer.CompressCalls);
    }

    [Fact]
    public async Task Keeps_original_when_transform_not_shorter()
    {
        // Transformer returns something longer than the original: middleware must keep original.
        var transformer = new FakeCavemanTransformer { CompressResult = LongText + LongText };
        var middleware = Create(transformer, EnabledOptions());

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal(LongText, TestPipeline.Content(result));
    }

    [Fact]
    public async Task Keeps_original_when_transform_fails_open()
    {
        var transformer = new FakeCavemanTransformer { CompressResult = null };
        var middleware = Create(transformer, EnabledOptions());

        var request = TestPipeline.Request("user", LongText);
        var result = await TestPipeline.RunAsync(middleware, request);

        Assert.Equal(LongText, TestPipeline.Content(result));
    }

    [Fact]
    public async Task Decompresses_response_when_enabled()
    {
        var transformer = new FakeCavemanTransformer
        {
            DecompressResult = "The fox jumps over the lazy dog in full prose.",
        };
        var options = EnabledOptions();
        options.CompressRequests = false;
        options.DecompressResponses = true;
        var middleware = Create(transformer, options);

        var compressedReply = string.Join(' ', Enumerable.Repeat("fox jump. dog lazy.", 5));
        var chunks = await RunWithResponseAsync(
            middleware,
            TestPipeline.Request("user", "hi"),
            new[]
            {
                new ChatResponseChunk { ContentDelta = compressedReply },
                new ChatResponseChunk { FinishReason = "stop", CompletionTokens = 12 },
            });

        var content = string.Concat(chunks.Select(c => c.ContentDelta));
        Assert.Contains("full prose", content);
        Assert.Equal(1, transformer.DecompressCalls);
        Assert.Contains(chunks, c => c.FinishReason == "stop");
        Assert.Contains(chunks, c => c.CompletionTokens == 12);
    }

    [Fact]
    public async Task Does_not_decompress_tool_call_responses()
    {
        var transformer = new FakeCavemanTransformer { DecompressResult = "should not be used" };
        var options = EnabledOptions();
        options.CompressRequests = false;
        options.DecompressResponses = true;
        var middleware = Create(transformer, options);

        var toolChunk = new ChatResponseChunk
        {
            ContentDelta = string.Join(' ', Enumerable.Repeat("call tool now.", 5)),
            ToolCalls = new List<System.Text.Json.JsonElement>
            {
                System.Text.Json.JsonDocument.Parse("{\"id\":\"1\"}").RootElement.Clone(),
            },
        };

        var chunks = await RunWithResponseAsync(
            middleware,
            TestPipeline.Request("user", "hi"),
            new[] { toolChunk });

        Assert.Equal(0, transformer.DecompressCalls);
        Assert.Single(chunks);
        Assert.NotNull(chunks[0].ToolCalls);
    }

    private static async Task<List<ChatResponseChunk>> RunWithResponseAsync(
        IChatMiddleware middleware,
        JsonObject request,
        IReadOnlyList<ChatResponseChunk> terminalChunks)
    {
        var context = TestPipeline.CreateContext(request);
        await middleware.InvokeAsync(context, ctx =>
        {
            ctx.ResponseChunks = ToAsync(terminalChunks);
            return Task.CompletedTask;
        });

        var collected = new List<ChatResponseChunk>();
        await foreach (var chunk in context.ResponseChunks)
        {
            collected.Add(chunk);
        }
        return collected;
    }

    private static async IAsyncEnumerable<ChatResponseChunk> ToAsync(IReadOnlyList<ChatResponseChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}

/// <summary>Deterministic <see cref="ICavemanTransformer"/> for exercising the middleware in isolation.</summary>
internal sealed class FakeCavemanTransformer : ICavemanTransformer
{
    public string? CompressResult { get; set; }
    public string? DecompressResult { get; set; }
    public int CompressCalls { get; private set; }
    public int DecompressCalls { get; private set; }

    public Task<string?> TransformAsync(
        string text,
        CavemanDirection direction,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (direction == CavemanDirection.Compress)
        {
            CompressCalls++;
            return Task.FromResult(CompressResult);
        }

        DecompressCalls++;
        return Task.FromResult(DecompressResult);
    }
}
