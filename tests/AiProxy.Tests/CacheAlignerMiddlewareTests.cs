using AiProxy.Pipeline.Middlewares;

namespace AiProxy.Tests;

public class CacheAlignerMiddlewareTests
{
    private readonly CacheAlignerMiddleware _middleware = new();

    [Fact]
    public async Task Replaces_uuid_in_system_prompt()
    {
        var request = TestPipeline.Request("system", "Session id: 550e8400-e29b-41d4-a716-446655440000.");

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        Assert.Equal("Session id: <UUID>.", content);
    }

    [Fact]
    public async Task Replaces_iso_timestamp_in_system_prompt()
    {
        var request = TestPipeline.Request("system", "Now: 2026-05-31T07:00:00Z done.");

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        Assert.Equal("Now: <TIMESTAMP> done.", content);
    }

    [Fact]
    public async Task Replaces_plain_date_in_system_prompt()
    {
        var request = TestPipeline.Request("system", "Today is 2026-05-31 ok.");

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        Assert.Equal("Today is <DATE> ok.", content);
    }

    [Fact]
    public async Task Replaces_epoch_seconds_in_system_prompt()
    {
        var request = TestPipeline.Request("system", "epoch=1717142400 end");

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        Assert.Equal("epoch=<EPOCH> end", content);
    }

    [Fact]
    public async Task Leaves_user_messages_untouched()
    {
        const string original = "My order 550e8400-e29b-41d4-a716-446655440000 placed 2026-05-31.";
        var request = TestPipeline.Request("user", original);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request));

        Assert.Equal(original, content);
    }

    [Fact]
    public async Task Leaves_non_volatile_text_unchanged()
    {
        const string original = "You are a helpful assistant. Be concise.";
        var request = TestPipeline.Request("system", original);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request));

        Assert.Equal(original, content);
    }
}
