using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// The write vocabulary and the read lexicon must describe the same set of relations.
/// </summary>
/// <remarks>
/// Two hand-maintained lists drift, and drift here is not cosmetic: a relation present only on the
/// read side is one the system will look for and can never have stored, which is a guaranteed miss
/// with no error anywhere. That is exactly how `assembled` came to be resolvable at query time while
/// the extractor, never having been offered the word, filed assembly under `completed` instead.
/// <para>
/// The canonical key set is therefore shared. Surface forms stay read-side only, deliberately: they
/// never enter an extraction prompt, where they would cost tokens across every call and invite the
/// extractor to choose inconsistently between `buy`, `buys` and `purchased` — the opposite of the
/// consolidation the vocabulary exists to produce.
/// </para>
/// </remarks>
public sealed class RelationVocabularyCoherenceTests
{
    [Fact]
    public void EveryRelationOfferedToExtractionIsResolvableAtQueryTime()
    {
        // The invariant in one line: anything we can write, we can find again. A relation the
        // extractor may store but the lexicon cannot resolve is unreachable by any question.
        foreach (var predicate in MemoryPredicateSeedVocabulary.Create().Snapshot())
        {
            MemoryRelationLexicon.Default.Resolve(predicate).Should()
                .Be(MemoryTripleCanonicalizer.Canonical(predicate),
                    $"'{predicate}' is offered to extraction and must resolve to itself");
        }
    }

    [Fact]
    public void EveryResolvableRelationIsOfferedToExtractionUnlessExplicitlyRetired()
    {
        // The other direction, and the one that was broken: 13 relations were resolvable but never
        // offered, so the graph could not contain them however well retrieval worked.
        var offered = MemoryPredicateSeedVocabulary.Create().Snapshot()
            .Select(MemoryTripleCanonicalizer.Canonical)
            .ToHashSet(StringComparer.Ordinal);
        var resolvable = MemoryRelationLexicon.Default.CanonicalRelations
            .Except(MemoryRelationSeedTable.RetiredRelations, StringComparer.Ordinal);

        resolvable.Should().BeSubsetOf(offered);
    }

    [Fact]
    public void TheTwoSidesShareOneSourceOfTruth()
    {
        var offered = MemoryPredicateSeedVocabulary.Create().Snapshot()
            .Select(MemoryTripleCanonicalizer.Canonical)
            .ToHashSet(StringComparer.Ordinal);
        var expected = MemoryRelationSeedTable.Table.Keys
            .Select(MemoryTripleCanonicalizer.Canonical)
            .Except(MemoryRelationSeedTable.RetiredRelations, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        offered.Should().BeEquivalentTo(expected);
    }

    [Fact]
    public void AssembledIsOfferedToExtraction()
    {
        // The named case. Assembly was stored as `completed` because the extractor was never offered
        // this word, which is half of why gpt4_15e38248 cannot be answered from the graph.
        MemoryPredicateSeedVocabulary.Create().Snapshot()
            .Select(MemoryTripleCanonicalizer.Canonical)
            .Should().Contain("assembled");
    }

    [Fact]
    public void RetiredRelationsStayResolvableSoOlderGraphsRemainReadable()
    {
        // A relation removed from the vocabulary does not vanish from graphs already written under it.
        // Retiring must stop new writes without making the existing facts unreachable.
        foreach (var retired in MemoryRelationSeedTable.RetiredRelations)
        {
            MemoryRelationLexicon.Default.Resolve(retired).Should().Be(retired);
            MemoryPredicateSeedVocabulary.Create().Snapshot()
                .Select(MemoryTripleCanonicalizer.Canonical)
                .Should().NotContain(retired);
        }
    }

    [Fact]
    public void OpposingRelationsSurviveTheSharedSource()
    {
        // Carried over from the seed, where it is load-bearing: offering only one side of an opposing
        // pair invites the extractor to collapse them and invert facts.
        var offered = MemoryPredicateSeedVocabulary.Create().Snapshot()
            .Select(MemoryTripleCanonicalizer.Canonical)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (left, right) in new[]
                 {
                     ("bought", "sold"), ("likes", "dislikes"),
                     ("borrowed", "lent"), ("gave", "received")
                 })
        {
            offered.Should().Contain(left).And.Contain(right);
        }
    }
}
