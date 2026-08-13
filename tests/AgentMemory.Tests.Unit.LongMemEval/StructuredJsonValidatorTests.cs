using AgentEval.Memory.External.LongMemEval;
using AgentEval.Memory.External.Models;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Reconciling a judge verdict under each protocol (3.7).
/// </summary>
/// <remarks>
/// <para>
/// The validator compares AgentEval's own <c>Correct</c> against <b>our re-parse of the free-text
/// explanation</c>. That cross-check exists to catch AgentEval's free-text parser mis-scoring — "yes
/// — there is no discrepancy" read as a no — which is a real, systematic failure.
/// </para>
/// <para>
/// <b>StructuredJson eliminates that failure at the source, and the check then becomes actively
/// wrong.</b> The explanation is no longer prose, so re-parsing it returns a wrong boolean and
/// rejects every question — which is exactly how the first StructuredJson run failed, on both arms.
/// Skipping it under that protocol removes a guard whose subject no longer exists; it does not relax
/// one that still applies, and these tests pin that distinction from both sides.
/// </para>
/// </remarks>
public sealed class StructuredJsonValidatorTests
{
    private static QuestionResult Question(string id, bool correct, string explanation) => new()
    {
        QuestionId = id,
        Question = "q",
        GoldAnswer = "gold",
        AgentResponse = "answer",
        Correct = correct,
        JudgeExplanation = explanation,
        QuestionType = "single-session-user",
        RawScore = correct ? 1d : 0d,
    };

    private static LongMemEvalQuestionTelemetry Telemetry(int n) =>
        new(n, 1, 1, false) { QuestionId = $"q-{n}" };

    private static LongMemEvalRunValidation Validate(
        JudgeVerdictProtocol protocol, params QuestionResult[] questions) =>
        LongMemEvalRunValidator.Validate(
            questionCount: questions.Length,
            llmCalls: questions.Length * 2,
            telemetry: Enumerable.Range(1, questions.Length).Select(Telemetry).ToList(),
            questionResults: questions,
            verdictProtocol: protocol);

    [Fact]
    public void FreeTextStillCatchesADisagreement()
    {
        // The guard must keep working where it applies. AgentEval recorded correct=true while its own
        // explanation says no -- the free-text mis-scoring this check exists for.
        var validation = Validate(
            JudgeVerdictProtocol.FreeText,
            Question("q-1", correct: true, explanation: "No, the answer is wrong."));

        validation.Issues.Should().Contain(i => i.Contains("disagree", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuredJsonDoesNotReParseTheExplanation()
    {
        // THE fix. A structured verdict carries no yes/no prose, so the same explanation that trips
        // the free-text path must not be re-parsed here -- doing so rejected every question in both
        // arms of the first StructuredJson run.
        var validation = Validate(
            JudgeVerdictProtocol.StructuredJson,
            Question("q-1", correct: true, explanation: "{\"verdict\":\"yes\"}"));

        validation.Issues.Should().NotContain(i => i.Contains("disagree", StringComparison.Ordinal));
    }

    [Fact]
    public void StructuredJsonDoesNotDemandAParseableVerdict()
    {
        // The sibling rejection: "AgentEval judge returned no usable verdict". Same root cause -- our
        // parser, not the judge -- and it must not fire where there is no prose to parse.
        var validation = Validate(
            JudgeVerdictProtocol.StructuredJson,
            Question("q-1", correct: false, explanation: string.Empty));

        validation.Issues.Should().NotContain(i => i.Contains("no usable verdict", StringComparison.Ordinal));
    }

    [Fact]
    public void FreeTextStillDemandsAParseableVerdict()
    {
        // And the same case under the protocol where it does apply: an unusable verdict is still a
        // rejection, because there the judge really did answer in prose we could not read.
        var validation = Validate(
            JudgeVerdictProtocol.FreeText,
            Question("q-1", correct: false, explanation: "…"));

        validation.Issues.Should().Contain(i => i.Contains("no usable verdict", StringComparison.Ordinal));
    }

    [Fact]
    public void TheDefaultProtocolIsFreeText()
    {
        // Every sealed base here is free-text, and the reconciliation must stay on for all of them.
        // A default that drifted would silently disable the guard for the runs it was written for.
        var validation = LongMemEvalRunValidator.Validate(
            questionCount: 1,
            llmCalls: 2,
            telemetry: [Telemetry(1)],
            questionResults: [Question("q-1", correct: true, explanation: "No, wrong.")]);

        validation.Issues.Should().Contain(i => i.Contains("disagree", StringComparison.Ordinal));
    }
}
