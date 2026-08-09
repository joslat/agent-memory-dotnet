using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// L10. A FAILED index blocks startup — but only if it is one of ours.
/// </summary>
/// <remarks>
/// The check exists because a failed index does not stop queries; they silently fall back to full
/// scans, so it surfaces as unexplained slowness rather than an error. Failing closed at bootstrap is
/// right.
/// <para>
/// But it currently fails on <b>any</b> FAILED index in the database, including ones AgentMemory
/// never created. On a shared Neo4j instance — the deployment the multi-tenant work exists to support
/// — an unrelated application's broken index becomes a startup crash in this library, which nobody
/// here can fix and which points at the wrong owner entirely.
/// </para>
/// <para>
/// Scoping to <c>SchemaConformance.ExpectedObjectNames</c> keeps every failure we are responsible for
/// while declining to fail on someone else's. That list is the same one <c>schema-check</c> uses, so
/// the two agree by construction.
/// </para>
/// </remarks>
public sealed class FailedIndexScopeTests
{
    private const int Dimensions = 1536;

    [Fact]
    public void OurOwnFailedIndexIsReported()
    {
        var ours = SchemaConformance.ExpectedObjectNames(Dimensions)[0];

        SchemaConformance.SelectOwnedFailures([$"{ours} (RANGE)"], Dimensions)
            .Should().ContainSingle().Which.Should().Contain(ours);
    }

    [Fact]
    public void AnotherApplicationsFailedIndexIsIgnored()
    {
        // The load-bearing case. A shared database is a supported deployment, and a neighbour's
        // broken index must not stop this library from starting.
        SchemaConformance.SelectOwnedFailures(["someone_elses_idx (RANGE)"], Dimensions)
            .Should().BeEmpty();
    }

    [Fact]
    public void OursIsStillFoundAmongUnrelatedFailures()
    {
        var ours = SchemaConformance.ExpectedObjectNames(Dimensions)[0];

        var selected = SchemaConformance.SelectOwnedFailures(
            ["unrelated_a (RANGE)", $"{ours} (RANGE)", "unrelated_b (LOOKUP)"], Dimensions);

        selected.Should().ContainSingle().Which.Should().Contain(ours);
    }

    [Fact]
    public void NothingFailedMeansNothingSelected()
    {
        SchemaConformance.SelectOwnedFailures([], Dimensions).Should().BeEmpty();
    }

    /// <summary>
    /// L10 x L11. The composite merge-key index is the one carrying the range-index key cap, so it is
    /// the index most likely to reach FAILED — and it must be recognised as ours when it does.
    /// </summary>
    /// <remarks>
    /// This holds because <c>ExpectedObjectNames</c> derives from <c>SchemaQueries.PropertyIndexes</c>
    /// rather than from a hand-kept list. The test exists so that deriving it stays a decision rather
    /// than an accident: if the two ever diverge, a failed <c>fact_merge_key_idx</c> would be silently
    /// attributed to another application and skipped.
    /// </remarks>
    [Fact]
    public void TheFactMergeKeyIndexIsRecognisedAsOurs()
    {
        SchemaConformance.ExpectedObjectNames(Dimensions).Should().Contain("fact_merge_key_idx");

        SchemaConformance.SelectOwnedFailures(["fact_merge_key_idx (RANGE)"], Dimensions)
            .Should().ContainSingle();
    }
}
