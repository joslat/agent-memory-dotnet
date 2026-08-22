using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Services;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The fan-out merge rules (30.10, design step 7).
/// </summary>
/// <remarks>
/// Pure and provider-free by design: these decide what actually reaches the prompt, so they are worth
/// testing without a container, a model, or a database standing between the rule and its assertion.
/// </remarks>
public sealed class RecallFanOutMergeTests
{
    private sealed record Row(string Id, string Text);

    private static (Row, double) R(string id, double score) => (new Row(id, id), score);

    private static (IReadOnlyList<Row> Merged, IReadOnlyList<string> Unique) Merge(
        IReadOnlyList<(Row, double)> monolithic,
        IReadOnlyList<(Row, double)> legs,
        int limit = 10) =>
        RecallFanOutMerge.Merge(monolithic, legs, row => row.Id, limit);

    [Fact]
    public void ALegsNewItemsAreAddedAndCountedAsContributions()
    {
        var (merged, unique) = Merge([R("a", 0.9)], [R("b", 0.8)]);

        merged.Select(r => r.Id).Should().BeEquivalentTo(["a", "b"]);
        unique.Should().Equal("b");
    }

    [Fact]
    public void ADuplicateIdIsKeptOnceAndIsNotAContribution()
    {
        // A leg re-finding what the blended query already had has contributed nothing, however many
        // times it returns it.
        var (merged, unique) = Merge([R("a", 0.5)], [R("a", 0.9), R("a", 0.7)]);

        merged.Should().ContainSingle();
        unique.Should().BeEmpty("re-finding an existing item is not a contribution");
    }

    [Fact]
    public void TheHigherScoreWins()
    {
        var (merged, _) = Merge([R("a", 0.5), R("b", 0.6)], [R("a", 0.99)]);

        // 'a' was ranked below 'b' at 0.5 and must now outrank it at 0.99.
        merged[0].Id.Should().Be("a");
    }

    [Fact]
    public void ALowerScoreFromALegNeverDemotesAnExistingItem()
    {
        var (merged, _) = Merge([R("a", 0.9)], [R("a", 0.1)]);

        merged.Should().ContainSingle();
        merged[0].Id.Should().Be("a");
    }

    [Fact]
    public void MonolithicPrecedenceBreaksTies()
    {
        // The load-bearing rule for distinguishability: a leg that re-finds items at identical scores
        // must leave the section byte-identical, not merely equivalent-but-reordered.
        var (merged, _) = Merge([R("a", 0.7), R("b", 0.7)], [R("c", 0.7)]);

        merged.Select(r => r.Id).Should().Equal("a", "b", "c");
    }

    [Fact]
    public void TheSectionIsRecappedAtTheExistingLimit()
    {
        // Budgets are never multiplied. A fan-out that returned more rows because it asked more
        // questions would "improve" recall by spending more of the prompt.
        var (merged, _) = Merge([R("a", 0.9), R("b", 0.8)], [R("c", 0.7), R("d", 0.6)], limit: 3);

        merged.Should().HaveCount(3);
    }

    [Fact]
    public void AContributionTruncatedAwayIsNotCounted()
    {
        // An item retrieved and then cut never reached the prompt. Counting it is exactly how a
        // mechanism looks valuable while changing nothing a reader ever sees.
        var (merged, unique) = Merge([R("a", 0.9), R("b", 0.8)], [R("z", 0.1)], limit: 2);

        merged.Select(r => r.Id).Should().Equal("a", "b");
        unique.Should().BeEmpty("z was retrieved but truncated away before the prompt");
    }

    [Fact]
    public void AContributionThatSurvivesTheCapIsCounted()
    {
        var (merged, unique) = Merge([R("a", 0.5)], [R("z", 0.95)], limit: 2);

        merged.Select(r => r.Id).Should().Equal("z", "a");
        unique.Should().Equal("z");
    }

    [Fact]
    public void NoLegsMeansTheMonolithicSectionIsUnchanged()
    {
        // The off-state shape: identical order, identical membership, zero contributions.
        var monolithic = new[] { R("a", 0.9), R("b", 0.8), R("c", 0.7) };

        var (merged, unique) = Merge(monolithic, []);

        merged.Select(r => r.Id).Should().Equal("a", "b", "c");
        unique.Should().BeEmpty();
    }

    [Fact]
    public void ARepeatedIdInTheMonolithicSetIsCollapsed()
    {
        var (merged, _) = Merge([R("a", 0.9), R("a", 0.4)], []);

        merged.Should().ContainSingle();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void ANonPositiveLimitYieldsNothing(int limit)
    {
        var (merged, unique) = Merge([R("a", 0.9)], [R("b", 0.8)], limit);

        merged.Should().BeEmpty();
        unique.Should().BeEmpty();
    }

    [Fact]
    public void EmptyOnBothSidesIsEmpty()
    {
        var (merged, unique) = Merge([], []);

        merged.Should().BeEmpty();
        unique.Should().BeEmpty();
    }

    // ── the affinity map ────────────────────────────────────────────────

    [Theory]
    [InlineData(MemoryTypeAffinity.Semantic, "entities", "facts")]
    [InlineData(MemoryTypeAffinity.Preference, "preferences")]
    [InlineData(MemoryTypeAffinity.Temporal, "facts", "messages")]
    [InlineData(MemoryTypeAffinity.Episodic, "messages")]
    [InlineData(MemoryTypeAffinity.Procedural, "traces")]
    public void EachAffinityMapsToItsSections(MemoryTypeAffinity affinity, params string[] expected)
    {
        SubQueryAffinityMap.SectionsFor(affinity).Should().Equal(expected);
    }

    [Fact]
    public void EverySectionTheMapPublishesIsOneTheAssemblerCanActuallyRetrieveFrom()
    {
        // R3. The map advertised "messages" and "traces" while the retrieval loop had arms only for
        // entities, facts and preferences -- so Episodic and Procedural legs paid for a live embedding
        // and could never return anything, reporting ItemsRetrieved=0 exactly as though the store had
        // been empty. Advertised-but-unreachable destinations are the mechanism-substituted shape the
        // router audit existed to kill, so the published set is pinned to the implemented set here.
        var implemented = new[] { "entities", "facts", "preferences", "messages", "traces" };

        var published = Enum.GetValues<MemoryTypeAffinity>()
            .SelectMany(SubQueryAffinityMap.SectionsFor)
            .Distinct()
            .ToArray();

        published.Should().OnlyContain(section => implemented.Contains(section),
            "a section the map names but the assembler cannot read is a leg billed for nothing");
    }

    [Fact]
    public void EveryDefinedAffinityHasAtLeastOneDestination()
    {
        // An affinity with no destination is a leg that costs an embedding and can never return
        // anything -- a silent no-op the report would still count as having run.
        foreach (var affinity in Enum.GetValues<MemoryTypeAffinity>())
        {
            SubQueryAffinityMap.SectionsFor(affinity).Should().NotBeEmpty(
                "affinity {0} would otherwise be billed and unable to retrieve", affinity);
        }
    }
}
