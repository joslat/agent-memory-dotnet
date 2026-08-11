using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// <c>FindByTriple</c> must ask the same question the write path answers.
/// </summary>
/// <remarks>
/// It looked up a triple with <c>toLower(f.subject) = toLower($subject)</c> on all three properties,
/// while the write path MERGEs on <c>{subject_key, predicate_key, object_key, owner_key}</c>. Those
/// are <b>different notions of the same triple</b>: <c>MemoryTripleCanonicalizer.CanonicalValue</c>
/// applies <c>ToLowerInvariant</c> <i>and collapses whitespace runs</i>, while Cypher's
/// <c>toLower</c> does neither — and the two disagree outright on U+0130, which is the documented
/// reason these keys are computed in C# rather than in Cypher. So the lookup could find a
/// <b>different fact than the MERGE would collapse onto</b>.
/// <para>
/// Matching the canonical keys fixes that, and is also the only shape that can use an index:
/// measured on 5.26, <c>toLower</c> plans a <c>NodeByLabelScan</c> while all four keys plan a
/// <c>NodeIndexSeek</c> returning one row. Three columns are not enough — this composite requires
/// every column filtered, not merely a prefix.
/// </para>
/// </remarks>
public sealed class FindByTripleMergeKeyTests
{
    [Fact]
    public void TheLookupMatchesTheSameKeysTheWritePathMergesOn()
    {
        var cypher = FactQueries.FindByTriple(hasOwnerFilter: true, includeShared: false);

        cypher.Should().Contain("f.subject_key = $subjectKey");
        cypher.Should().Contain("f.predicate_key = $predicateKey");
        cypher.Should().Contain("f.object_key = $objectKey");
    }

    [Fact]
    public void NoToLowerRemains()
    {
        // toLower is what made this unindexable: Neo4j 5 has no functional indexes, so any predicate
        // wrapped in a function forces a scan no matter what is indexed.
        FactQueries.FindByTriple(hasOwnerFilter: true, includeShared: false)
            .Should().NotContain("toLower");
        FactQueries.FindByTriple(hasOwnerFilter: false, includeShared: true)
            .Should().NotContain("toLower");
    }

    [Fact]
    public void AnOwnerScopedLookupFiltersOwnerKeySoAllFourColumnsArePresent()
    {
        // The seek only happens with ALL FOUR columns. Three is a scan - measured, not assumed.
        var cypher = FactQueries.FindByTriple(hasOwnerFilter: true, includeShared: false);

        cypher.Should().Contain("f.owner_key = $ownerKey");
    }

    [Fact]
    public void TheUnscopedLookupDoesNotPretendToSeek()
    {
        // With no owner scope there is no fourth column to filter, so this plan stays a scan. It
        // still gains the correctness fix and drops three per-row toLower calls, but no seek is
        // claimed for it.
        var cypher = FactQueries.FindByTriple(hasOwnerFilter: false, includeShared: true);

        cypher.Should().NotContain("owner_key");
        cypher.Should().Contain("f.subject_key = $subjectKey");
    }

    [Fact]
    public void SharedFactsRemainReachableWhenIncludeSharedIsSet()
    {
        // Owner isolation must not tighten. A shared fact is stored with the shared owner key, so an
        // include-shared lookup has to admit both it and the caller's own.
        var cypher = FactQueries.FindByTriple(hasOwnerFilter: true, includeShared: true);

        cypher.Should().Contain("$ownerKey");
        cypher.Should().Contain("$sharedOwnerKey");
    }
}
