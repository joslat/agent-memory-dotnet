using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Reading the judge's verdict, including prefixes nobody hardcoded.
/// </summary>
/// <remarks>
/// The parser stripped exactly two prefixes — "Judge said:" and "Judge outcome:" — and required the
/// next letter-token to be yes or no. A judge that phrases its verdict any other way is reported as
/// "returned no valid yes/no verdict", which rejects the whole arm and discards a run.
/// <para>
/// That is not hypothetical and it is not the judge being wrong. Question <c>dad224aa</c> was
/// rejected in <b>2 of 5</b> identical n=50 repeats, and the diagnostic captured
/// <c>FailureKind=unparseable, RejectedToken="Judge"</c> — the judge produced a verdict in a third
/// "Judge…:" shape and our parser could not read it. On one of those runs the retry recovered the
/// same question with a valid verdict, which is the clearest possible evidence the judgement was
/// fine and the parsing was not.
/// </para>
/// <para>
/// The fix stays conservative: a leading prefix is only stripped when doing so actually yields a
/// yes/no verdict, so tolerance cannot manufacture a verdict out of a hedge.
/// </para>
/// </remarks>
public sealed class JudgeVerdictParsingTests
{
    [Theory]
    [InlineData("yes")]
    [InlineData("Yes, the answer matches the reference.")]
    [InlineData("Judge said: yes")]
    [InlineData("Judge outcome: yes")]
    [InlineData("Judge verdict: yes")]     // the shape that cost two runs
    [InlineData("Judgement: yes")]
    [InlineData("Judgment: YES — the times agree.")]
    public void ACorrectVerdictIsReadWhateverPrefixTheJudgeUses(string explanation)
    {
        LongMemEvalRunValidator.TryParseJudgeVerdict(explanation, out var correct)
            .Should().BeTrue($"'{explanation}' states a verdict");
        correct.Should().BeTrue();
    }

    [Theory]
    [InlineData("no")]
    [InlineData("Judge verdict: no")]
    [InlineData("Judge outcome: No, the answer omits the amount.")]
    public void AnIncorrectVerdictIsReadWhateverPrefixTheJudgeUses(string explanation)
    {
        LongMemEvalRunValidator.TryParseJudgeVerdict(explanation, out var correct)
            .Should().BeTrue();
        correct.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Partially correct.")]
    [InlineData("Judge verdict: partially correct")]
    [InlineData("Judge could not determine an answer")]
    [InlineData("The answer is correct.")]
    [InlineData("maybe: yes")]
    public void AnythingThatIsNotAYesOrNoStaysInvalid(string explanation)
    {
        // The guard the tolerance must not defeat. Widening the prefix handling must never turn a
        // hedge into a verdict — an unreadable judgement has to stay unreadable, because inventing
        // one silently scores a question nobody judged.
        LongMemEvalRunValidator.TryParseJudgeVerdict(explanation, out _).Should().BeFalse();
    }
}
