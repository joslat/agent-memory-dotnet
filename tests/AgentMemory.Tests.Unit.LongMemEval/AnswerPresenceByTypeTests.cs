using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The answer-presence gate must be read per question type, because "absent" means two things.
/// </summary>
/// <remarks>
/// Adjudicated on 2026-08-10 against the judge's own evidence. Of two questions the gate reported
/// absent, one was a real extraction failure and one was not:
/// <list type="bullet">
/// <item><description><c>eeda8a6d</c> — genuinely missing. The agent said so itself
/// (<i>"I don't have information about a second aquarium"</i>) and the judge scored it wrong. The
/// gate was right.</description></item>
/// <item><description><c>1f2b8d4f</c> — answered correctly from memory alone
/// (<i>"The difference is $750 — your boots cost $800, the budget pair $50"</i>), yet reported absent.
/// The gold answer is a <b>computed</b> value: 750 is 800 − 50, and memory stores the two prices,
/// never their difference. Its distinctive token cannot appear in memory even though the question is
/// perfectly answerable.</description></item>
/// </list>
/// So the gate is a floor for <b>extractive</b> answers only. Grouping by question type separates
/// "absent because not stored" from "absent because derived" without changing the metric — the raw
/// verdict stays exactly as measured, and only its interpretation gains the missing axis.
/// </remarks>
public sealed class AnswerPresenceByTypeTests
{
    private static LongMemEvalQuestionTelemetry Q(
        int n, string type, bool checkable, bool present) =>
        new(n, 0, 10, false)
        {
            QuestionId = $"q{n}",
            QuestionType = type,
            AnswerPresence = new LongMemEvalAnswerPresenceResult(checkable, present, [], present ? 1 : 0),
        };

    [Fact]
    public void PresenceIsReportedPerQuestionType()
    {
        var summary = LongMemEvalAnswerPresence.SummariseByType(
        [
            Q(1, "single-session-user", checkable: true, present: true),
            Q(2, "single-session-user", checkable: true, present: false),
            Q(3, "temporal-reasoning", checkable: true, present: false),
        ]);

        summary.Should().ContainKey("single-session-user");
        summary["single-session-user"].Checkable.Should().Be(2);
        summary["single-session-user"].Present.Should().Be(1);
        summary["temporal-reasoning"].Present.Should().Be(0);
    }

    [Fact]
    public void UncheckableQuestionsAreCountedSeparatelyAndNeverAsAbsent()
    {
        // "We cannot tell" must not inflate the absent count — that is what turns a floor into a
        // false alarm, which is the whole failure this grouping exists to expose.
        var summary = LongMemEvalAnswerPresence.SummariseByType(
        [
            Q(1, "knowledge-update", checkable: false, present: false),
            Q(2, "knowledge-update", checkable: true, present: true),
        ]);

        summary["knowledge-update"].Total.Should().Be(2);
        summary["knowledge-update"].Checkable.Should().Be(1);
        summary["knowledge-update"].Present.Should().Be(1);
        summary["knowledge-update"].Absent.Should().Be(0);
    }

    [Fact]
    public void AQuestionWithoutATypeIsGroupedRatherThanDropped()
    {
        // Dropping it would silently shrink the denominator, which is how a metric stops adding up.
        var summary = LongMemEvalAnswerPresence.SummariseByType(
            [Q(1, null!, checkable: true, present: false)]);

        summary.Should().ContainKey("unknown");
        summary["unknown"].Absent.Should().Be(1);
    }

    [Fact]
    public void QuestionsWithNoGateResultAreExcludedEntirely()
    {
        // The probe is optional. A question it never ran on is not evidence of anything.
        var noGate = new LongMemEvalQuestionTelemetry(1, 0, 10, false)
        {
            QuestionId = "q1",
            QuestionType = "single-session-user",
        };

        LongMemEvalAnswerPresence.SummariseByType([noGate]).Should().BeEmpty();
    }
}
