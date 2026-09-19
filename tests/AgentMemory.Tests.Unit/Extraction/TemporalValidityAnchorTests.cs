using AgentMemory.Abstractions.Options;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Validity windows must be dated from the CONVERSATION, not from the machine running extraction.
/// </summary>
/// <remarks>
/// <para>
/// Measured 2026-09-16 on the prospective corpus with <c>TemporalValidityMode.Extract</c>: 41
/// extracted windows, <b>0 before today, 26 exactly today, 15 in the future</b> — against
/// conversations anchored months in the past. The instruction asked for ISO-8601 dates without
/// saying what "now" was, so the model dated them from its own notion of the present.
/// </para>
/// <para>
/// The reference was always in the prompt: every turn is rendered with its own ISO-8601 timestamp,
/// unconditionally. Nothing pointed the model at it. This is the same defect shape as provenance
/// reading the clock at write time and firing reading it at recall time — <b>the time that is easy
/// to reach substituted for the time that matters</b>, three times in one codebase.
/// </para>
/// </remarks>
public sealed class TemporalValidityAnchorTests
{
    private static string Instruction(TemporalValidityMode mode) =>
        ExtractionPromptSemantics.TemporalValidityInstruction(mode);

    /// <summary>The instruction names the turn's own timestamp as the anchor.</summary>
    [Fact]
    public void TheExtractInstructionAnchorsRelativeDatesToTheTurnThatStatesThem()
    {
        var text = Instruction(TemporalValidityMode.Extract);

        text.Should().Contain("TIMESTAMP OF THE TURN THAT STATES IT",
            "without a stated reference the model dates relative expressions from its own present, "
            + "which produced 41 of 41 windows at today or later on a corpus set in the past");
        text.Should().Contain("never the Monday after today");
    }

    /// <summary>
    /// The instruction is still EMPTY when the mode is Ignore, so no sealed measurement moves.
    /// </summary>
    /// <remarks>
    /// Prompt bytes are fingerprinted into every measured run in this track. This string is safe to
    /// change only because it appears under <c>Extract</c> alone, and nothing could set that mode
    /// until 2026-09-16 — so no recorded number was ever taken with it present.
    /// </remarks>
    [Fact]
    public void IgnoreEmitsNothingSoEveryRecordedFingerprintIsUnchanged()
    {
        Instruction(TemporalValidityMode.Ignore).Should().BeEmpty();
    }

    /// <summary>The refusal to guess an expiry survives the rewrite.</summary>
    /// <remarks>
    /// The original instruction's most important clause: an unbounded fact recorded as expiring is
    /// worse than one recorded as permanent. Adding an anchor must not cost that.
    /// </remarks>
    [Fact]
    public void TheRefusalToGuessAnExpiryIsUnchanged()
    {
        var text = Instruction(TemporalValidityMode.Extract);

        text.Should().Contain("never guess an expiry");
        text.Should().Contain("worse than one recorded as permanent");
    }
}
