using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Making <c>StructuredJson</c> reachable — and impossible to use invisibly (PLAN 3.7).
/// </summary>
/// <remarks>
/// <para>
/// AgentEval's free-text verdict parser <i>"vetoes a leading yes when the word no appears later in
/// the response"</i>, so a judge answering "yes — there is no discrepancy" is scored as a failure.
/// That is a systematic mis-scoring, and <c>StructuredJson</c> is the actual fix.
/// </para>
/// <para>
/// <b>It was also unreachable.</b> The protocol parameter existed on <c>CreateOptions</c> and every
/// call site took the default, so no run could ever select it — the dead-option shape.
/// </para>
/// <para>
/// <b>The default must not move.</b> The same AgentEval docs state results under StructuredJson are
/// not comparable with a free-text base, and every sealed base in this track is free-text. Flipping
/// the default would not produce a wrong number; it would produce a <i>better</i> one that silently
/// invalidates every comparison drawn against the existing runs.
/// </para>
/// </remarks>
public sealed class JudgeVerdictProtocolWiringTests
{
    private static JudgeVerdictProtocol Parse(string? value) =>
        LongMemEvalPreparedPairProgram.ParseJudgeProtocolForTests(value);

    [Fact]
    public void TheDefaultIsFreeText()
    {
        // Every sealed base here was scored under FreeText. This is the assertion that stops a later
        // change making the numbers better and the comparisons meaningless.
        Parse(null).Should().Be(JudgeVerdictProtocol.FreeText);
        Parse("").Should().Be(JudgeVerdictProtocol.FreeText);
    }

    [Theory]
    [InlineData("structured-json")]
    [InlineData("structuredjson")]
    [InlineData("json")]
    [InlineData("STRUCTURED-JSON")]
    public void StructuredJsonIsReachable(string value) =>
        Parse(value).Should().Be(JudgeVerdictProtocol.StructuredJson);

    [Fact]
    public void AnUnknownProtocolIsRejectedRatherThanDefaulted()
    {
        // Silently falling back to FreeText would mean a run the operator believed was StructuredJson
        // produced a free-text score under a StructuredJson label -- the worst of both, and invisible.
        var act = () => Parse("strict");

        act.Should().Throw<ArgumentException>().WithMessage("*free-text, structured-json*");
    }

    [Fact]
    public void TheBenchmarkOptionsCarryTheChoiceThrough()
    {
        // The wiring, not the parsing: a protocol parsed and then dropped before CreateOptions would
        // pass every test above while every run stayed free-text.
        var options = LongMemEvalBenchmarkProtocol.CreateOptions(
            datasetPath: "dataset.json",
            questions: 10,
            seed: 42,
            judgeRetryAttempts: 2,
            evidenceDetail: LongMemEvalEvidenceDetail.Identifiers,
            maxRelevantMessages: 20,
            verdictProtocol: JudgeVerdictProtocol.StructuredJson);

        options.JudgeVerdictProtocol.Should().Be(JudgeVerdictProtocol.StructuredJson);
    }
}
