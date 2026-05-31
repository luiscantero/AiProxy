using AiProxy.Pipeline.Middlewares;

namespace AiProxy.Tests;

public class LogCompressorMiddlewareTests
{
    private readonly LogCompressorMiddleware _middleware = new();

    [Fact]
    public async Task Collapses_consecutive_duplicate_log_lines()
    {
        var log = string.Join('\n',
            "2026-05-31 10:00:00 INFO heartbeat ok",
            "2026-05-31 10:00:01 INFO heartbeat ok",
            "2026-05-31 10:00:02 INFO heartbeat ok",
            "2026-05-31 10:00:03 INFO heartbeat ok",
            "2026-05-31 10:00:04 INFO heartbeat ok");
        var request = TestPipeline.Request("user", log);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        Assert.Contains("(×5 identical lines)", content);
        Assert.True(content.Length < log.Length);
    }

    [Fact]
    public async Task Always_preserves_error_and_stack_trace_lines()
    {
        var log = string.Join('\n',
            "2026-05-31 10:00:00 INFO startup",
            "2026-05-31 10:00:01 INFO startup",
            "2026-05-31 10:00:02 INFO startup",
            "2026-05-31 10:00:03 INFO startup",
            "2026-05-31 10:00:04 ERROR NullReferenceException thrown",
            "    at MyApp.Service.DoWork()");
        var request = TestPipeline.Request("user", log);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        Assert.Contains("ERROR NullReferenceException thrown", content);
        Assert.Contains("at MyApp.Service.DoWork()", content);
    }

    [Fact]
    public async Task Thins_long_runs_of_low_severity_lines()
    {
        var lines = new List<string>();
        for (var i = 0; i < 20; i++)
            lines.Add($"2026-05-31 10:00:{i:D2} INFO processing item {i}");
        var log = string.Join('\n', lines);
        var request = TestPipeline.Request("user", log);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request))!;

        Assert.Contains("low-severity log lines", content);
        Assert.True(content.Length < log.Length);
    }

    [Fact]
    public async Task Leaves_short_content_unchanged()
    {
        var log = string.Join('\n',
            "2026-05-31 10:00:00 INFO a",
            "2026-05-31 10:00:01 INFO b");
        var request = TestPipeline.Request("user", log);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request));

        Assert.Equal(log, content);
    }

    [Fact]
    public async Task Leaves_non_log_prose_unchanged()
    {
        var prose = string.Join('\n',
            "Dear team,",
            "I wanted to share an update on the project.",
            "We are making good progress this week.",
            "Please review the attached document.",
            "Best regards.");
        var request = TestPipeline.Request("user", prose);

        var content = TestPipeline.Content(await TestPipeline.RunAsync(_middleware, request));

        Assert.Equal(prose, content);
    }
}
