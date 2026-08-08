using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// J2.1. A query-side map from the verbs a question uses to the canonical predicates a graph stores.
/// </summary>
/// <remarks>
/// Read-side only, and that asymmetry is the whole safety argument: predicate clustering over stored
/// facts was rejected because merging <c>bought</c> onto <c>sold</c> corrupts meaning irreversibly,
/// whereas a wrong entry here costs precision on one query and never alters a stored fact.
/// </remarks>
public sealed class MemoryRelationLexiconTests
{
    private static MemoryRelationLexicon Lexicon => MemoryRelationLexicon.Default;

    [Fact]
    public void TheFourVerbsOfTheKnownFailingQuestionResolveToFourDistinctRelations()
    {
        // gpt4_15e38248: "How many pieces of furniture did I buy, assemble, sell, or fix". Expansion
        // only ever pulled predicates that similarity happened to surface, so three of the four were
        // never expanded. This is the J1.5 acceptance case, checked at the layer that resolves them.
        var resolved = Lexicon.ResolveQuestion(
            "How many pieces of furniture did I buy, assemble, sell, or fix this year?");

        resolved.Should().HaveCount(4);
        resolved.Should().OnlyHaveUniqueItems();
    }

    [Theory]
    [InlineData("buy", "bought")]
    [InlineData("buys", "bought")]
    [InlineData("buying", "bought")]
    [InlineData("bought", "bought")]
    [InlineData("purchased", "bought")]
    [InlineData("assemble", "assembled")]
    [InlineData("assembling", "assembled")]
    [InlineData("fix", "fixed")]
    [InlineData("repaired", "fixed")]
    [InlineData("sell", "sold")]
    public void InflectionsAndSynonymsResolveToOneCanonicalRelation(string surface, string canonical) =>
        Lexicon.Resolve(surface).Should().Be(canonical);

    [Theory]
    // The safety property that must never regress. These are one embedding threshold apart and mean
    // opposite things; collapsing them would invert a fact at read time.
    [InlineData("bought", "sold")]
    [InlineData("likes", "dislikes")]
    [InlineData("borrowed", "lent")]
    [InlineData("gave", "received")]
    public void OpposingRelationsNeverResolveTogether(string left, string right) =>
        Lexicon.Resolve(left).Should().NotBe(Lexicon.Resolve(right));

    [Fact]
    public void AnUnknownVerbResolvesToNothingSoTheCallerCanFallBack()
    {
        // The fallback to today's top-K-derived predicates is what makes this change unable to be
        // worse than current behaviour, and it depends on an honest miss.
        Lexicon.Resolve("defenestrated").Should().BeNull();
    }

    [Fact]
    public void NoSurfaceFormMapsToTwoCanonicalRelations()
    {
        // Ambiguity is dropped, never guessed. The shipped table must contain none at all, so this
        // asserts the drop list is empty rather than merely that lookups are single-valued.
        MemoryRelationLexicon.Default.AmbiguousSurfaceForms.Should().BeEmpty();
    }

    [Fact]
    public void EveryCanonicalRelationIsAlreadyInStoredPredicateKeyForm()
    {
        // Resolution is worthless if it produces keys the graph cannot match. Stored predicate_key is
        // lowercase with separators folded to single spaces.
        foreach (var canonical in MemoryRelationLexicon.Default.CanonicalRelations)
        {
            canonical.Should().Be(MemoryTripleCanonicalizer.Canonical(canonical));
            canonical.Should().NotContain("_").And.NotContain("  ");
        }
    }

    [Fact]
    public void MultiWordRelationsAreResolved()
    {
        // Measured in the real graph: "is interested in" (50 facts), "asked about" (38),
        // "works at" (16). A single-token harvest would miss all of them.
        Lexicon.Resolve("is interested in").Should().Be("is interested in");
        Lexicon.ResolveQuestion("What did I ask about and what am I interested in?")
            .Should().Contain("asked about").And.Contain("is interested in");
    }

    [Fact]
    public void ShortWordsAreNotDestroyedByStemming()
    {
        // "is" is the single most common predicate in the measured graph at 1,213 facts, 26% of all
        // of them. Naive -s stripping would reduce it to "i" and lose a quarter of the graph.
        MemoryTripleCanonicalizer.Canonical("is").Should().Be("is");
        Lexicon.StoredFormsOf("is").Should().Contain("is").And.Contain("was");
    }

    [Theory]
    // Independent review finding, verified against 50 natural questions: these fire on almost every
    // question a person asks, and each one expands `is` - 1,213 facts, 26% of the measured graph -
    // consuming the shared expansion budget before any correct relation is reached. They cannot
    // simply be deleted, because expansion still needs them as stored predicate keys.
    [InlineData("is")]
    [InlineData("was")]
    [InlineData("were")]
    [InlineData("have")]
    [InlineData("had")]
    [InlineData("been")]
    public void CopulasDoNotTriggerRetrievalFromAQuestion(string form) =>
        Lexicon.Resolve(form).Should().BeNull();

    [Fact]
    public void CopulasAreStillReachableAsStoredKeys()
    {
        // The other half of the same requirement: a fact stored under "was" must still be fetched
        // when `is` is expanded, or the split would lose data rather than protect the budget.
        Lexicon.StoredFormsOf("is").Should().Contain("was").And.Contain("were");
        Lexicon.StoredFormsOf("has").Should().Contain("had");
    }

    [Theory]
    // Independent review, verified: the stop list was checked against the raw form only, then the
    // stemmer looked up its result WITHOUT re-checking it. So a suppressed bare verb was handed
    // straight back through its own inflections - "Let me know if that works" resolved `works at`,
    // pulling 28 predicate keys into a 100-fact budget. This silently weakened every suppression
    // decision in the vocabulary, including any made later.
    [InlineData("works")]
    [InlineData("working")]
    [InlineData("needing")]
    public void SuppressionSurvivesTheStemmer(string form) =>
        Lexicon.Resolve(form).Should().BeNull();

    [Fact]
    public void SuppressedVerbsAreStillReachableThroughTheirQuestionAnchor()
    {
        // The other half: suppression must cost no recall. The anchored phrase wins by
        // longest-phrase-first before the bare verb is ever tried.
        Lexicon.ResolveQuestion("Where do i work?").Should().Contain("works at");
        Lexicon.ResolveQuestion("What did i plan for the weekend?").Should().Contain("planned");
    }

    [Fact]
    public void AssistantBoilerplateResolvesToNothing()
    {
        // The probes that motivated the whole storedOnly mechanism.
        Lexicon.ResolveQuestion("Let me know if that works").Should().BeEmpty();
        Lexicon.ResolveQuestion("Give me the top five items").Should().BeEmpty();
        Lexicon.ResolveQuestion("Order the results by date").Should().BeEmpty();
    }

    [Fact]
    public void AQuestionAboutABirthDoesNotExpandTheCopula()
    {
        // The reviewer's worked example: this previously resolved `is` and pulled a quarter of the
        // graph into a fixed budget.
        Lexicon.ResolveQuestion("When was my daughter born?")
            .Should().NotContain("is").And.Contain("was born");
    }

    [Fact]
    public void ResolutionIsCaseAndSeparatorInsensitive()
    {
        Lexicon.Resolve("  BOUGHT ").Should().Be("bought");
        Lexicon.Resolve("travelled_to").Should().Be("travelled to");
    }

    [Fact]
    public void AQuestionWithNoRecognisableRelationResolvesToNothing()
    {
        Lexicon.ResolveQuestion("What is the airspeed velocity of an unladen swallow?")
            .Should().NotContain("bought");
    }

    [Fact]
    public void ARelationExpandsToEveryStoredFormOfItself()
    {
        // Measured in the real graph: "planned" holds 839 facts and "plans" holds 14, as SEPARATE
        // predicate keys, because the write-side canonicalizer folds case and separators but never
        // morphology. Expanding on the canonical name alone would silently miss the smaller bucket,
        // which is the exact completeness failure expansion exists to prevent.
        var forms = Lexicon.StoredFormsOf("planned");

        forms.Should().Contain("planned").And.Contain("plans").And.Contain("plan");
    }

    [Theory]
    // Every one of these is a real stored predicate key in the measured graph that is an inflection
    // of a bigger bucket. Each must be reachable from its canonical relation.
    [InlineData("is", "was")]
    [InlineData("wants", "wanted")]
    [InlineData("uses", "used")]
    [InlineData("considered", "is considering")]
    [InlineData("has", "had")]
    public void InflectedStoredKeysAreReachableFromTheirCanonicalRelation(
        string canonical, string storedVariant) =>
        Lexicon.StoredFormsOf(canonical).Should().Contain(storedVariant);

    [Fact]
    public void ExpandingAnUnknownRelationYieldsOnlyItself()
    {
        // So a caller can expand uniformly without special-casing relations the table never saw.
        Lexicon.StoredFormsOf("defenestrated").Should().Equal("defenestrated");
    }

    [Fact]
    public void ResolvedRelationsAreDistinctAndOrderedByFirstAppearance()
    {
        var resolved = Lexicon.ResolveQuestion("Did I sell or buy or sell anything?");

        resolved.Should().Equal("sold", "bought");
    }
}
