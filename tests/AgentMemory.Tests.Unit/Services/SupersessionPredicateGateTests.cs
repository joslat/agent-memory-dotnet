using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// The gate that decides whether write-time supersession runs at all.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> The 30.9d render arm produced zero supersession notes in 60 of 60 prompts,
/// and a live-Neo4j walk of the chain showed every downstream link working: the edge is written, the
/// predecessor lookup finds it, and BOTH live and as-of recall render the note. The one link that
/// walk bypassed was this gate — it superseded through the repository directly, so it never asked
/// whether extraction would have been *allowed* to supersede.
/// </para>
/// <para>
/// <c>PersistenceStage.SupersedeReplacedFactsAsync</c> returns immediately unless
/// <c>WriteTimeFactResolution.CanSupersede</c> says the predicate is single-valued, and that is
/// decided by a vocabulary of exactly six relations with the <c>single</c> cardinality —
/// <c>belongs to, costs, expires, lives in, weighs, works at</c> — with the documented behaviour
/// "false for anything unrecognised".
/// </para>
/// <para>
/// This matters far beyond one arm: a corpus whose predicates fall outside those six cannot produce a
/// single <c>:SUPERSEDED_BY</c> edge, so <c>SupersedeReplacedFacts=true</c> is indistinguishable from
/// <c>false</c> on it — a lever that reports as ON while doing nothing.
/// </para>
/// </remarks>
public class SupersessionPredicateGateTests
{
    [Theory]
    [InlineData("works at")]
    [InlineData("lives in")]
    [InlineData("belongs to")]
    public void TheDeclaredFunctionalRelationsCanSupersede(string predicate) =>
        MemoryRelationCardinality.IsSingleValued(predicate).Should().BeTrue();

    /// <summary>
    /// The phrasings a free-form extractor produces for "which department / site / branch was X at".
    /// </summary>
    /// <remarks>
    /// The evaluation profile leaves <c>usePredicateVocabulary</c> off, so the extractor is not
    /// constrained to the canonical set and writes whatever the sentence suggests. Each of these is a
    /// perfectly reasonable extraction of a corrected-location statement, and each one silently
    /// disables supersession for the fact it belongs to.
    /// </remarks>
    [Theory]
    [InlineData("was at")]
    [InlineData("is at")]
    [InlineData("assigned to")]
    [InlineData("located in")]
    [InlineData("department")]
    [InlineData("branch")]
    [InlineData("region")]
    [InlineData("office")]
    [InlineData("site")]
    [InlineData("recorded at")]
    public void TheCorpusShapedPhrasingsCannotSupersede(string predicate) =>
        MemoryRelationCardinality.IsSingleValued(predicate).Should().BeFalse(
            "an unrecognised predicate silently makes SupersedeReplacedFacts a no-op");

    /// <summary>The gate's reach is six relations, and that number is the finding.</summary>
    [Fact]
    public void OnlySixRelationsInTheEntireVocabularyAreSingleValued()
    {
        MemoryRelationCardinality.SingleValuedPredicates.Should().HaveCount(6);
        MemoryRelationCardinality.SingleValuedPredicates.Should().BeEquivalentTo(
            ["belongs to", "costs", "expires", "lives in", "weighs", "works at"]);
    }
}
