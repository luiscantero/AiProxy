using AiProxy.Pipeline;

namespace AiProxy.Tests;

public class ModelPriorityTests
{
    [Theory]
    [InlineData("claude-opus-5", "Opus")]
    [InlineData("claude-sonnet-5", "Sonnet")]
    [InlineData("gemini-3.6-flash", "Flash")]
    [InlineData("gpt-5.6-luna", "Luna")]
    [InlineData("gpt-5.6-terra", "Terra")]
    [InlineData("gpt-4o", "4o")]                     // nothing but the family and a variant
    [InlineData("o3-mini", "O3-Mini")]               // unknown family: keep both segments
    [InlineData("llama3.1:8b", "Llama3.1-8b")]       // ollama-style tag
    [InlineData("claude-3-5-sonnet-20241022", "Sonnet")]
    [InlineData("gpt-5.6", "Gpt")]                   // family only: better than an empty label
    [InlineData("5.6", "5.6")]                       // version only: nothing to shorten
    public void DeriveLabel_shortens_model_ids(string model, string expected) =>
        ModelPriority.DeriveLabel(model).Should().Be(expected);

    [Theory]
    [InlineData("claude-opus-5", "claude-opus-5")]   // the id itself
    [InlineData("CLAUDE-OPUS-5", "claude-opus-5")]   // case-insensitive
    [InlineData("Opus", "claude-opus-5")]            // the short label
    [InlineData("Opus", "claude-opus-6")]            // labels survive a version bump
    [InlineData("sonnet-4.5", "claude-3-5-sonnet-20241022")] // a partial id
    public void Matches_accepts_ids_labels_and_partial_ids(string entry, string model) =>
        ModelPriority.Matches(entry, model).Should().BeTrue();

    [Theory]
    [InlineData("Opus", "claude-sonnet-5")]
    [InlineData("claude-opus-5", "claude-sonnet-5")]
    [InlineData("", "claude-opus-5")]
    public void Matches_rejects_unrelated_models(string entry, string model) =>
        ModelPriority.Matches(entry, model).Should().BeFalse();

    [Fact]
    public void Rank_returns_the_position_of_the_matching_entry()
    {
        var priority = new[] { "Opus", "Sonnet", "Haiku" };

        ModelPriority.Rank(priority, "claude-opus-5").Should().Be(0);
        ModelPriority.Rank(priority, "claude-sonnet-5").Should().Be(1);
        ModelPriority.Rank(priority, "claude-haiku-5").Should().Be(2);
    }

    [Fact]
    public void Rank_returns_Unranked_for_a_model_nothing_names()
    {
        var priority = new[] { "Opus" };

        ModelPriority.Rank(priority, "llama3.1:8b").Should().Be(ModelPriority.Unranked);
    }

    [Fact]
    public void Rank_is_Unranked_when_no_priority_is_configured() =>
        ModelPriority.Rank(Array.Empty<string>(), "claude-opus-5")
            .Should().Be(ModelPriority.Unranked);

    [Fact]
    public void Rank_prefers_an_exact_id_over_a_looser_entry_above_it()
    {
        // "gpt-4o" also names "gpt-4o-mini" by segment subset, but the explicit id wins.
        var priority = new[] { "gpt-4o", "gpt-4o-mini" };

        ModelPriority.Rank(priority, "gpt-4o-mini").Should().Be(1);
        ModelPriority.Rank(priority, "gpt-4o").Should().Be(0);
    }

    [Fact]
    public void Order_puts_the_strongest_first_and_keeps_unranked_models_at_the_end()
    {
        var priority = new[] { "Opus", "Sonnet" };
        var models = new[] { "local-a", "claude-sonnet-5", "local-b", "claude-opus-5" };

        ModelPriority.Order(priority, models)
            .Should().Equal("claude-opus-5", "claude-sonnet-5", "local-a", "local-b");
    }

    [Fact]
    public void Order_leaves_the_incoming_order_alone_when_no_priority_is_configured()
    {
        var models = new[] { "b", "a", "c" };

        ModelPriority.Order(Array.Empty<string>(), models).Should().Equal("b", "a", "c");
    }

    [Fact]
    public void Prune_removes_entries_that_name_no_connected_model()
    {
        var priority = new List<string> { "Opus", "Retired", "Sonnet" };

        var removed = ModelPriority.Prune(priority, new[] { "claude-opus-5", "claude-sonnet-5" });

        removed.Should().Equal("Retired");
        priority.Should().Equal("Opus", "Sonnet");
    }
}
