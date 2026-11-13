using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// ILC-29: with evidence-based identity, within-extraction deduplication merges exact copies only. A copy has the same
/// words, so identity ties it to the same people (bindings follow a fact's own words within one extraction); different
/// wording stays, because identity keeps each original phrasing.
/// </summary>
public sealed class WithinExtractionExactMergeTests
{
    private static PreparedFact Fact(string obj, string? replaces = null, string predicate = "lives in", DateTimeOffset? occurredOn = null) =>
        new(new ExtractedFact { Subject = "Alice", Predicate = predicate, Object = obj, Confidence = 0.9, ReplacesId = replaces, OccurredOn = occurredOn },
            [1f, 0f]);

    [Fact]
    public void Two_exact_copies_become_one_fact_that_keeps_both_closing_targets()
    {
        var (kept, merged) = WithinExtractionFactDeduplication.MergeExact([Fact("Lisbon", "old-home"), Fact("Lisbon", "old-plan")]);

        kept.Should().ContainSingle("one telling is one mention");
        kept[0].WithClosingProposals().Select(f => f.ReplacesId).Should().BeEquivalentTo(["old-home", "old-plan"]);
        merged.Should().ContainSingle().Which.Effect.Should().Be(MemoryWriteEffect.MergedWithinExtraction);
    }

    [Fact]
    public void Different_wording_stays_apart()
    {
        var (kept, merged) = WithinExtractionFactDeduplication.MergeExact(
            [Fact("my physical presence", predicate: "requires"), Fact("my physical presence in Córdoba", predicate: "requires")]);

        kept.Should().HaveCount(2, "identity keeps each original phrasing; only exact copies are one statement");
        merged.Should().BeEmpty();
    }

    [Fact]
    public void The_same_words_with_different_dates_stay_apart()
    {
        var (kept, _) = WithinExtractionFactDeduplication.MergeExact(
            [Fact("Lisbon", occurredOn: DateTimeOffset.Parse("2026-01-01T00:00:00Z")), Fact("Lisbon", occurredOn: DateTimeOffset.Parse("2026-06-01T00:00:00Z"))]);

        kept.Should().HaveCount(2);
    }
}
