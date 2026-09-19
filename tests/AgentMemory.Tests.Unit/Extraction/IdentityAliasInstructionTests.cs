using AgentMemory.Abstractions.Options;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// E-1. The alias instruction reaches EVERY extraction rung, or it reaches the wrong one.
/// </summary>
/// <remarks>
/// The schema has always required an <c>aliases</c> field; the multi-session prompt's only example of
/// it is <c>"aliases":[]</c>, directly under "use empty arrays when a category has no supported
/// memory". The per-kind rung said "include aliases when mentioned" and the batch rung — the one the
/// benchmark runs — said nothing at all. A semantic carried by one rung and not its twin is the defect
/// this whole class of test exists to stop.
/// </remarks>
public sealed class IdentityAliasInstructionTests
{
    private static string Batch(bool capture) =>
        LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
            vocabulary: null,
            AssistantContentMode.Ignore,
            TemporalValidityMode.Ignore,
            ExtractionProvenanceMode.Batch,
            capture);

    private static string Unified(bool capture) =>
        LlmUnifiedMemoryExtractor.BuildSystemPrompt(
            AssistantContentMode.Ignore,
            LlmEntityExtractor.DefaultEntityTypes,
            TemporalValidityMode.Ignore,
            ExtractionProvenanceMode.Batch,
            capture);

    /// <summary>On, the instruction is present and says what an alias IS.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheBatchRungCarriesTheInstructionExactlyWhenAsked(bool capture)
    {
        var prompt = Batch(capture);

        prompt.Contains("aliases", StringComparison.Ordinal).Should().BeTrue(
            "the schema names the field either way");
        prompt.Contains("STATES that two names refer to one thing", StringComparison.Ordinal)
            .Should().Be(capture);
    }

    /// <summary>The unified rung is not allowed to differ from the batch one.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheUnifiedRungCarriesTheSameInstruction(bool capture)
    {
        Unified(capture).Contains("STATES that two names refer to one thing", StringComparison.Ordinal)
            .Should().Be(capture);
    }

    /// <summary>
    /// OFF is byte-for-byte the old prompt, so every sealed measurement stays comparable.
    /// </summary>
    [Fact]
    public void OffAppendsNothingAtAll()
    {
        ExtractionPromptSemantics.IdentityAliasInstruction(capture: false).Should().BeEmpty();
    }

    /// <summary>
    /// The instruction forbids inferring identity from resemblance.
    /// </summary>
    /// <remarks>
    /// This is the Goodhart guard written into the prompt. A model told merely to "find aliases"
    /// merges things that look alike, and two distinct referents recorded as one cannot be separated
    /// again — the failure mode is silent and permanent, and it inflates exactly the counting
    /// questions the feature is measured on.
    /// </remarks>
    [Fact]
    public void TheInstructionRequiresTheConversationToHaveSaidSo()
    {
        var instruction = ExtractionPromptSemantics.IdentityAliasInstruction(capture: true);

        instruction.Should().Contain("ONLY when the conversation says the two are the same");
        instruction.Should().Contain("never merge two names because they look or sound similar");
    }
}
