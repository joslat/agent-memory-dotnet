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

        prompt.Should().Contain("was_born");
        prompt.Should().Contain("predicate");
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
