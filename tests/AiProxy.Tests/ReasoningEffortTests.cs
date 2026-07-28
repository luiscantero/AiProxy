using AiProxy.Pipeline;
using AiProxy.Storage;

namespace AiProxy.Tests;

public class ReasoningEffortTests
{
    private static readonly ReasoningEffortOptions Enabled = new() { Enabled = true };

    private static ModelInfo Reasoning(params string[] levels) => new() { ReasoningEfforts = levels };

    [Fact]
    public void Expand_publishes_the_model_once_per_detected_level()
    {
        ReasoningEffort.Expand("gpt-5.6-sol", Reasoning("low", "medium", "high"), Enabled)
            .Should().Equal("gpt-5.6-sol:low", "gpt-5.6-sol:medium", "gpt-5.6-sol:high");
    }

    [Fact]
    public void Expand_narrows_to_the_configured_levels()
    {
        var options = new ReasoningEffortOptions { Enabled = true, Levels = { "medium", "high", "max" } };

        ReasoningEffort.Expand("claude-opus-5", Reasoning("low", "medium", "high", "xhigh", "max"), options)
            .Should().Equal("claude-opus-5:medium", "claude-opus-5:high", "claude-opus-5:max");
    }

    [Fact]
    public void Expand_skips_configured_levels_a_model_does_not_have()
    {
        var options = new ReasoningEffortOptions { Enabled = true, Levels = { "medium", "high", "max" } };

        // gemini-shaped: no "max", so it simply drops out instead of being published and rejected.
        ReasoningEffort.Expand("gemini-3.6-flash", Reasoning("minimal", "low", "medium", "high"), options)
            .Should().Equal("gemini-3.6-flash:medium", "gemini-3.6-flash:high");
    }

    [Fact]
    public void Expand_leaves_a_model_bare_when_the_filter_excludes_every_level()
    {
        var options = new ReasoningEffortOptions { Enabled = true, Levels = { "max" } };

        ReasoningEffort.Expand("gpt-5-mini", Reasoning("low", "medium", "high"), options)
            .Should().Equal("gpt-5-mini");
    }

    [Fact]
    public void Expand_follows_whatever_levels_the_upstream_advertises()
    {
        ReasoningEffort.Expand("gpt-5.6-sol", Reasoning("minimal", "xhigh"), Enabled)
            .Should().Equal("gpt-5.6-sol:minimal", "gpt-5.6-sol:xhigh");
    }

    [Fact]
    public void Expand_leaves_models_without_a_selectable_effort_alone()
    {
        ReasoningEffort.Expand("claude-opus-5", Reasoning(), Enabled).Should().Equal("claude-opus-5");
    }

    [Fact]
    public void Expand_leaves_models_the_provider_reported_nothing_about_alone()
    {
        ReasoningEffort.Expand("claude-opus-5", null, Enabled).Should().Equal("claude-opus-5");
    }

    [Fact]
    public void Expand_leaves_every_model_alone_when_disabled()
    {
        ReasoningEffort.Expand("gpt-5.6-sol", Reasoning("low", "high"), new ReasoningEffortOptions())
            .Should().Equal("gpt-5.6-sol");
    }

    [Theory]
    [InlineData("gpt-5.6-sol:high", "gpt-5.6-sol", "high")]
    [InlineData("llama3.1:8b", "llama3.1", "8b")]
    public void TrySplit_separates_the_id_at_its_last_colon(string requested, string model, string suffix)
    {
        ReasoningEffort.TrySplit(requested, out var actualModel, out var actualSuffix).Should().BeTrue();
        actualModel.Should().Be(model);
        actualSuffix.Should().Be(suffix);
    }

    [Theory]
    [InlineData("gpt-5.6-sol")]     // no suffix at all
    [InlineData("gpt-5.6-sol:")]    // empty suffix
    [InlineData(":high")]           // no model
    public void TrySplit_rejects_ids_without_two_halves(string requested)
    {
        ReasoningEffort.TrySplit(requested, out var model, out _).Should().BeFalse();
        model.Should().Be(requested);
    }

    [Fact]
    public void MatchLevel_returns_the_upstream_spelling()
    {
        ReasoningEffort.MatchLevel(Reasoning("low", "high"), "HIGH", Enabled).Should().Be("high");
    }

    [Theory]
    [InlineData("medium")]  // a level this model does not accept
    [InlineData("8b")]      // a colon that is part of an id, not an effort
    public void MatchLevel_rejects_levels_the_model_does_not_accept(string suffix)
    {
        ReasoningEffort.MatchLevel(Reasoning("low", "high"), suffix, Enabled).Should().BeNull();
    }

    [Fact]
    public void MatchLevel_ignores_the_publishing_filter()
    {
        var options = new ReasoningEffortOptions { Enabled = true, Levels = { "high" } };

        ReasoningEffort.MatchLevel(Reasoning("low", "high"), "low", options).Should().Be("low");
    }

    [Fact]
    public void MatchLevel_is_inert_when_disabled()
    {
        ReasoningEffort.MatchLevel(Reasoning("high"), "high", new ReasoningEffortOptions()).Should().BeNull();
    }
}
