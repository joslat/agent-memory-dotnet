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

    /// <summary>
    /// Every prompt an extractor can send, so a new rung cannot quietly skip a shared setting.
    /// </summary>
    /// <remarks>
    /// The first version of this test checked only the unified rung and passed while the
    /// <b>multi-session batch</b> extractor ignored the setting entirely — a defect that shipped in
    /// the same commit that added the setting. Enumerating all three rungs is what makes the
    /// conformance rule enforceable rather than aspirational.
    /// </remarks>
    private static IEnumerable<(string Rung, string Prompt)> AllRungPrompts(TemporalValidityMode mode)
    {
        var options = new LlmExtractionOptions();
        yield return ("per-kind fact",
            LlmFactExtractor.BuildSystemPrompt(AssistantContentMode.Ignore, mode));
        yield return ("unified",
            LlmUnifiedMemoryExtractor.BuildSystemPrompt(
                AssistantContentMode.Ignore, options.EntityTypes, mode));
        yield return ("multi-session batch",
            LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
                vocabulary: null, AssistantContentMode.Ignore, mode));
    }

    [Fact]
    public void EveryExtractorRungCarriesTheInstructionWhenExtractIsOn()
    {
        // A setting only some extractors respect is worse than no setting: it makes behaviour depend
        // on a performance flag, which is precisely what the shared-semantics type exists to prevent.
        var expected = ExtractionPromptSemantics.TemporalValidityInstruction(
            TemporalValidityMode.Extract);

        foreach (var (rung, prompt) in AllRungPrompts(TemporalValidityMode.Extract))
        {
            prompt.Should().Contain(expected, $"the {rung} rung must honour TemporalValidityMode");
        }
    }

    [Fact]
    public void NoExtractorRungChangesItsPromptWhenIgnoreIsSet()
    {
        // The byte-for-byte guarantee, checked on every rung rather than assumed from one. The batch
        // rung additionally uses its prompt for TOKEN ACCOUNTING, so an instruction it appends but
        // does not count would make the frozen batch plan under-estimate by exactly that text.
        foreach (var (rung, prompt) in AllRungPrompts(TemporalValidityMode.Ignore))
        {
            prompt.Should().NotContain("valid_until", $"the {rung} rung must add nothing when Ignore");
        }
    }
}
