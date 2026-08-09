using AgentMemory.Core.Memory;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// G3B.14 wiring. The extractor invents a predicate per sentence because nothing tells it which
/// relations exist: 700 facts under 421 distinct predicates, with one birth expressed five different
/// ways. Offering the established vocabulary in the prompt is the root fix — normalise at generation
/// rather than trying to reconcile phrasings afterwards, which cannot be done safely
/// (<c>bought</c>/<c>sold</c>).
/// </summary>
public sealed class ExtractionPredicateVocabularyPromptTests
{
    [Fact]
    public void TheSeedVocabularyIsOfferedToTheExtractor()
    {
        var prompt = LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
            MemoryPredicateSeedVocabulary.Create());

        // Asserted in the canonical space form rather than the former `was_born` spelling. The seed is
        // now derived from the one shared relation table, whose keys are written in stored
        // predicate_key form. This is a deliberate change and not merely a test edit: both spellings
        // fold to the identical predicate_key, so what reaches the graph is unchanged, and the natural
        // phrase is the better thing to put in front of a language model.
        prompt.Should().Contain("was born");
        prompt.Should().Contain("predicate");
        // The invariant the test actually exists for: every offered relation appears in the prompt.
        foreach (var relation in MemoryPredicateSeedVocabulary.Create().Snapshot())
            prompt.Should().Contain(relation);
    }

    [Fact]
    public void ThePromptIsUnchangedWhenNoVocabularyIsOffered()
    {
        // The frozen plan's token totals depend on prompt size, so an empty vocabulary must not
        // silently alter the contract for callers that do not use this.
        var withoutVocabulary = LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
            new MemoryPredicateVocabulary());

        withoutVocabulary.Should().NotContain("Established relation");
    }

    [Fact]
    public void ThePromptIsReproducibleForAGivenVocabulary()
    {
        // Injected text that reordered per call would make extraction irreproducible for reasons
        // unrelated to the model - the exact failure that made an earlier score sequence
        // unattributable.
        var vocabulary = MemoryPredicateSeedVocabulary.Create();

        LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(vocabulary).Should()
            .Be(LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(vocabulary));
    }

    [Fact]
    public void TheSeedIsCuratedAndDoesNotFoldOpposites()
    {
        // The seed is reviewed, not mined, precisely so opposite relations both survive.
        var seed = MemoryPredicateSeedVocabulary.Create().Snapshot();

        seed.Should().Contain("bought").And.Contain("sold");
        seed.Should().Contain("likes").And.Contain("dislikes");
    }

    [Fact]
    public void TheExtractorIsInstructedToReuseRatherThanReplace()
    {
        // A model told to use *only* these relations would drop facts that genuinely need a new one.
        var prompt = LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
            MemoryPredicateSeedVocabulary.Create());

        prompt.Should().MatchRegex("(?i)(reuse|prefer)");
    }
}
