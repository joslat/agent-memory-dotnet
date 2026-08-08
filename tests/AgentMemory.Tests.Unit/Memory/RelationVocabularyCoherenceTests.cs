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
        //
        // The one exception is deliberate and explicit. A handful of relations are declared query
        // stop forms because they fire on almost every question and expand a bucket large enough to
        // exhaust the retrieval budget - `is` alone is 26% of the measured graph. Those are still
        // written, and still fetched by similarity and by expansion; they simply never TRIGGER an
        // expansion. The exception is enumerated in the artifact, not inferred here.
        foreach (var predicate in MemoryPredicateSeedVocabulary.Create().Snapshot())
        {
            var canonical = MemoryTripleCanonicalizer.Canonical(predicate);
            if (MemoryRelationSeedTable.QueryStopForms.Contains(canonical))
                continue;
            MemoryRelationLexicon.Default.Resolve(predicate).Should()
                .Be(canonical, $"'{predicate}' is offered to extraction and must resolve to itself");
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
        // A relation removed from the vocabulary does not vanish from graphs already written under
        // it. Retiring must stop new writes without making the existing facts unreachable, so the
        // retired name survives as a surface form of the relation it merged into — reachable through
        // the survivor rather than through a canonical key that no longer exists.
        foreach (var retired in MemoryRelationSeedTable.RetiredRelations)
        {
            MemoryRelationLexicon.Default.Resolve(retired).Should().NotBeNullOrEmpty(
                "facts stored under a retired relation must stay reachable");
            MemoryRelationLexicon.Default.CanonicalRelations.Should().NotContain(retired);
            MemoryPredicateSeedVocabulary.Create().Snapshot()
                .Select(MemoryTripleCanonicalizer.Canonical)
                .Should().NotContain(retired);
        }
    }

    [Fact]
    public void NoInflectionOfAListedFormResolvesToADifferentRelation()
    {
        // Reviewer finding 4. The stemmer strips suffixes, so a listed form's siblings can land on a
        // DIFFERENT relation by accident. This is the mechanical gate that catches such a collision at
        // build time instead of as a wrong retrieval in production.
        var lexicon = MemoryRelationLexicon.Default;
        var collisions = new List<string>();

        foreach (var relation in lexicon.CanonicalRelations)
        {
            foreach (var form in lexicon.StoredFormsOf(relation))
            {
                foreach (var inflection in new[] { form + "s", form + "ing", form + "ed" })
                {
                    var resolved = lexicon.Resolve(inflection);
                    // A miss is fine - not every inflection is real English. Landing on another
                    // relation is not.
                    if (resolved is not null && resolved != relation)
                        collisions.Add($"'{inflection}' (from '{relation}') resolves to '{resolved}'");
                }
            }
        }

        collisions.Should().BeEmpty();
    }

    [Fact]
    public void TheFamilyOfEveryRelationIsDeclaredAndUsable()
    {
        // Reviewer finding 3: the field was internally inconsistent, marking durable dispositions as
        // events. A field a reviewer cannot trust is worse than no field.
        var document = RelationVocabularyDocument.Load();

        foreach (var stative in new[] { "likes", "prefers", "wants", "owns", "knows", "is" })
            document.Canonical[stative].Family.Should().Be("state");
        foreach (var episodic in new[] { "bought", "sold", "travelled to", "called" })
            document.Canonical[episodic].Family.Should().Be("event");
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
