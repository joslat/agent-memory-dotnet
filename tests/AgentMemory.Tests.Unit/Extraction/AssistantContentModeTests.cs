using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Memory;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// What extraction is told to do with assistant turns — and that all three extractors are told the same.
/// </summary>
/// <remarks>
/// Two defects motivate these tests.
/// <para>
/// <b>The behaviour.</b> Extraction produced only user-centric facts: the assistant's recommendation
/// existed nowhere in the graph, so "what did you suggest?" was unanswerable. That is now a setting,
/// because which treatment is best is an empirical question rather than a settled one.
/// </para>
/// <para>
/// <b>The architecture.</b> The three extractors are three rungs of a cost-optimisation ladder that
/// are supposed to be interchangeable, but each rung rewrote its prompt from scratch and the
/// semantics were never carried forward — the per-kind fact extractor says "skip opinions" and the
/// two unified prompts say nothing at all. Nothing tested that they agree. The conformance test here
/// is the guard: a setting that only some extractors honour is worse than no setting, because it
/// makes behaviour depend on a performance flag.
/// </para>
/// </remarks>
public sealed class AssistantContentModeTests
{
    [Fact]
    public void IgnoreIsTheDefault()
    {
        // Every measurement to date was taken under this behaviour. Changing it silently would make
        // prior results incomparable without anyone noticing.
        new LlmExtractionOptions().AssistantContent.Should().Be(AssistantContentMode.Ignore);
    }

    [Fact]
    public void IgnoreAddsNothingAtAll()
    {
        // Byte-for-byte identical, so an existing sealed base stays comparable. Same discipline the
        // predicate vocabulary already follows when empty.
        ExtractionPromptSemantics.AssistantContentInstruction(AssistantContentMode.Ignore)
            .Should().BeEmpty();
    }

    [Fact]
    public void UtteranceAsksForTheActAndForbidsAssertingItsTruth()
    {
        var instruction = ExtractionPromptSemantics.AssistantContentInstruction(
            AssistantContentMode.Utterance);

        instruction.Should().NotBeEmpty();
        // The load-bearing distinction: record THAT it was said, never THAT it is true.
        instruction.Should().Contain("assistant");
        instruction.Should().MatchRegex("(?i)do not.*(true|fact|assert)");
    }

    [Fact]
    public void FactModeAsksForTheClaimItself()
    {
        var instruction = ExtractionPromptSemantics.AssistantContentInstruction(
            AssistantContentMode.Fact);

        instruction.Should().NotBeEmpty();
        instruction.Should().NotBe(
            ExtractionPromptSemantics.AssistantContentInstruction(AssistantContentMode.Utterance),
            "the two modes must ask for materially different things or the setting is decorative");
    }

    /// <summary>The structural guard: all three rungs must honour the same setting.</summary>
    [Theory]
    [InlineData(AssistantContentMode.Utterance)]
    [InlineData(AssistantContentMode.Fact)]
    public void EveryExtractorHonoursTheSetting(AssistantContentMode mode)
    {
        var expected = ExtractionPromptSemantics.AssistantContentInstruction(mode);

        var prompts = new[]
        {
            LlmFactExtractor.BuildSystemPrompt(mode),
            LlmUnifiedMemoryExtractor.BuildSystemPrompt(mode),
            LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
                MemoryPredicateSeedVocabulary.Create(), mode),
        };

        foreach (var prompt in prompts)
            prompt.Should().Contain(expected);
    }

    [Theory]
    [InlineData(AssistantContentMode.Ignore)]
    public void EveryExtractorLeavesItsPromptUntouchedWhenIgnoring(AssistantContentMode mode)
    {
        // Reproducing today's prompt exactly is what keeps existing sealed bases comparable.
        LlmFactExtractor.BuildSystemPrompt(mode)
            .Should().Be(LlmFactExtractor.DefaultSystemPrompt);
        LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(null, mode)
            .Should().Be(LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(null));
    }
}
