using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// Facts already deduplicate on <c>{subject, predicate, object, owner_key}</c>, but that key uses raw
/// strings, so surface differences defeat it. These cover the canonical form that repairs it.
/// </summary>
public sealed class MemoryTripleCanonicalizerTests
{
    [Theory]
    // The exact pairs measured on a real extracted graph as separate nodes for one fact.
    [InlineData("were_born_in", "were born in")]
    [InlineData("User", "user")]
    [InlineData("was_born", "Was Born")]
    [InlineData("  recently_had  ", "recently had")]
    [InlineData("is-planning", "is planning")]
    public void SurfaceVariantsOfOneRelationCollapseToOneKey(string left, string right) =>
        MemoryTripleCanonicalizer.Canonical(left).Should()
            .Be(MemoryTripleCanonicalizer.Canonical(right));

    [Theory]
    // Deterministic only: relations that differ in meaning must never merge, however similar they
    // look or read. `bought`/`sold` is the case that would silently invert a fact.
    [InlineData("bought", "sold")]
    [InlineData("was born in", "was born after")]
    [InlineData("likes", "dislikes")]
    [InlineData("welcomed", "welcomes")]
    public void RelationsThatDifferInMeaningStayDistinct(string left, string right) =>
        MemoryTripleCanonicalizer.Canonical(left).Should()
            .NotBe(MemoryTripleCanonicalizer.Canonical(right));

    [Fact]
    public void RunsOfSeparatorsCollapseToASingleSpace() =>
        MemoryTripleCanonicalizer.Canonical("was___born  \t in").Should().Be("was born in");

    [Fact]
    public void LeadingAndTrailingSeparatorsAreRemoved() =>
        MemoryTripleCanonicalizer.Canonical("__was born__").Should().Be("was born");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("___")]
    public void EmptyInputYieldsAnEmptyKeyRatherThanThrowing(string? value) =>
        MemoryTripleCanonicalizer.Canonical(value).Should().BeEmpty();

    [Fact]
    public void TheCanonicalFormIsStable()
    {
        // It becomes a persisted merge key, so it must never drift between calls or releases.
        var once = MemoryTripleCanonicalizer.Canonical("Were_Born_In");
        var twice = MemoryTripleCanonicalizer.Canonical(once);

        twice.Should().Be(once);
        once.Should().Be("were born in");
    }

    [Fact]
    public void DistinctObjectsAreNotFoldedTogether()
    {
        // Near-duplicate objects ("a few weeks before the session" vs "before the potluck") are one
        // event phrased twice. Collapsing them is a judgement call, so it is deliberately NOT done
        // here — that belongs to retrieval-time capping, where it is reversible.
        MemoryTripleCanonicalizer.Canonical("a few weeks before the session").Should()
            .NotBe(MemoryTripleCanonicalizer.Canonical("a few weeks before the potluck"));
    }

    [Theory]
    // Audit finding: separator folding is right for predicates and CORRUPTING for values. A fact
    // recording minus five would have MERGEd onto five, silently merging a quantity with its
    // negation — from a helper written to prevent silent duplication.
    [InlineData("-5", "5")]
    [InlineData("-1", "1")]
    [InlineData("well-being", "well being")]
    [InlineData("e-mail", "e mail")]
    public void ValueCanonicalizationNeverRewritesPunctuation(string left, string right) =>
        MemoryTripleCanonicalizer.CanonicalValue(left).Should()
            .NotBe(MemoryTripleCanonicalizer.CanonicalValue(right));

    [Fact]
    public void ValueCanonicalizationStillFoldsCaseAndWhitespace()
    {
        // It must still collapse the differences that carry no meaning, or dedup stops working.
        MemoryTripleCanonicalizer.CanonicalValue("  The   Blue  Sofa ").Should()
            .Be(MemoryTripleCanonicalizer.CanonicalValue("the blue sofa"));
    }

    [Fact]
    public void PredicateCanonicalizationStillFoldsSeparators() =>
        MemoryTripleCanonicalizer.Canonical("was_born").Should()
            .Be(MemoryTripleCanonicalizer.Canonical("was born"));
}
