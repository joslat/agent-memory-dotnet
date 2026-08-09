using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// G3B.14. Canonicalization collapses spelling; it cannot collapse phrasing. Measured on a live
/// graph, one real-world event — a birth — was expressed as <c>was born</c>, <c>was born in</c>,
/// <c>were born in</c>, <c>had</c> and <c>welcomed</c>, so retrieving any single relation gathered at
/// most 3 of 5. The extractor invents a predicate per sentence because nothing tells it which
/// relations already exist. This is that vocabulary.
/// </summary>
public sealed class MemoryPredicateVocabularyTests
{
    [Fact]
    public void AFamiliarRelationReusesTheEstablishedPredicate()
    {
        var vocabulary = new MemoryPredicateVocabulary();

        var first = vocabulary.Admit("was_born");
        var second = vocabulary.Admit("Was Born");

        second.Should().Be(first, "spelling variants must resolve to one established relation");
        vocabulary.Count.Should().Be(1);
    }

    [Fact]
    public void TheFirstSpellingWinsSoTheVocabularyIsStable()
    {
        // Later arrivals must not rewrite an established predicate, or the graph's relation names
        // would drift between runs and no query could rely on them.
        var vocabulary = new MemoryPredicateVocabulary();

        vocabulary.Admit("was_born").Should().Be("was_born");
        vocabulary.Admit("WAS BORN").Should().Be("was_born");
    }

    [Fact]
    public void GenuinelyDifferentRelationsAreBothAdmitted()
    {
        // Deterministic only. "bought" and "sold" are one embedding threshold apart and opposite in
        // meaning; nothing here may fold them together.
        var vocabulary = new MemoryPredicateVocabulary();

        vocabulary.Admit("bought");
        vocabulary.Admit("sold");
        vocabulary.Admit("likes");
        vocabulary.Admit("dislikes");

        vocabulary.Count.Should().Be(4);
    }

    [Fact]
    public void TheVocabularyIsOfferedToTheExtractorInAStableOrder()
    {
        // It is injected into a prompt, so a set whose order changed per call would make extraction
        // non-reproducible for reasons unrelated to the model.
        var vocabulary = new MemoryPredicateVocabulary();
        vocabulary.Admit("welcomed");
        vocabulary.Admit("was_born");
        vocabulary.Admit("had");

        vocabulary.Snapshot().Should().Equal(vocabulary.Snapshot());
        vocabulary.Snapshot().Should().BeInAscendingOrder();
    }

    [Fact]
    public void GrowthIsBoundedSoAPathologicalRunCannotExhaustThePrompt()
    {
        // Without a cap the vocabulary grows toward one predicate per fact — measured at 421 for 700
        // facts — and injecting that would consume the extraction budget it is meant to improve.
        var vocabulary = new MemoryPredicateVocabulary(maximumSize: 3);

        vocabulary.Admit("one");
        vocabulary.Admit("two");
        vocabulary.Admit("three");
        var overflow = vocabulary.Admit("four");

        vocabulary.Count.Should().Be(3);
        overflow.Should().Be("four", "an unadmitted predicate is still usable, just not established");
        vocabulary.Snapshot().Should().NotContain("four");
    }

    [Fact]
    public void BlankPredicatesAreRejectedRatherThanEstablished()
    {
        var vocabulary = new MemoryPredicateVocabulary();

        vocabulary.Admit("   ").Should().BeEmpty();
        vocabulary.Count.Should().Be(0);
    }
}
