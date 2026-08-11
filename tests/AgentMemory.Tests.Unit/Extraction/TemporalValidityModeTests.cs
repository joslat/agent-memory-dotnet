using AgentMemory.Abstractions.Options;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Prospective memory — "this is true until Friday" — has substrate but no writer.
/// </summary>
/// <remarks>
/// <c>ExtractedFact.ValidFrom</c> and <c>ValidUntil</c> exist, `Fact` nodes carry `valid_from` and
/// `valid_until`, and the bitemporal recall path already reads them. But <b>no extractor has ever
/// populated them and no prompt has ever mentioned validity</b>, so the columns are empty on every
/// fact ever stored. The memory map calls this "prospective — expression only", and that is the whole
/// gap: a representation nothing writes.
/// <para>
/// <see cref="TemporalValidityMode"/> closes it the same way <see cref="AssistantContentMode"/> closed
/// the episodic gap — a toggle, default off, whose off-state prompt is byte-for-byte what shipped
/// before. Prompt bytes are a measured variable: they are fingerprinted into every run and feed the
/// frozen batch plan's token accounting, so a default that shifted them would invalidate sealed bases
/// silently.
/// </para>
/// </remarks>
public sealed class TemporalValidityModeTests
{
    [Fact]
    public void IgnoreIsTheDefaultAndAddsNothingToThePrompt()
    {
        // The invariant that keeps every existing base and quality number valid.
        new LlmExtractionOptions().TemporalValidity.Should().Be(TemporalValidityMode.Ignore);

        ExtractionPromptSemantics.TemporalValidityInstruction(TemporalValidityMode.Ignore)
            .Should().BeEmpty();
    }

    [Fact]
    public void ExtractAsksForValidityAndSaysWhatToDoWhenThereIsNone()
    {
        // "Omit when unbounded" is the load-bearing half. Without it the model invents an expiry for
        // every fact, and a fact that wrongly expires is worse than one that never expires: live
        // recall filters on these columns, so a fabricated valid_until silently deletes a memory from
        // every future answer.
        var instruction = ExtractionPromptSemantics.TemporalValidityInstruction(
            TemporalValidityMode.Extract);

        instruction.Should().NotBeEmpty();
        instruction.Should().Contain("valid_from");
        instruction.Should().Contain("valid_until");
        instruction.Should().Contain("Omit");
    }

    [Theory]
    [InlineData(TemporalValidityMode.Ignore)]
    [InlineData(TemporalValidityMode.Extract)]
    public void EveryExtractorHonoursTheSetting(TemporalValidityMode mode)
    {
        // The conformance rule this file inherits: a setting only some extractors respect is worse
        // than no setting, because it makes behaviour depend on a performance flag. The unified path
        // must carry the same instruction the per-kind path does.
        var expected = ExtractionPromptSemantics.TemporalValidityInstruction(mode);

        var unified = LlmUnifiedMemoryExtractor.BuildSystemPrompt(
            AssistantContentMode.Ignore, new LlmExtractionOptions().EntityTypes, mode);

        if (expected.Length == 0)
        {
            unified.Should().Be(LlmUnifiedMemoryExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, new LlmExtractionOptions().EntityTypes));
        }
        else
        {
            unified.Should().Contain(expected);
        }
    }
}
