using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Grouping a run by memory type rather than by task label, and attributing its failures.
/// </summary>
public sealed class TypedBreakdownTests
{
    private static LongMemEvalQuestionTelemetry Q(
        int number, string id, string taskType, bool? present, bool checkable = true) =>
        new(number, 20, 10, false)
        {
            QuestionId = id,
            QuestionType = taskType,
            AnswerPresence = present is null
                ? null
                : new LongMemEvalAnswerPresenceResult(checkable, present.Value, [], present.Value ? 1 : 0),
        };

    [Fact]
    public void QuestionsAreGroupedByMemoryTypeNotTaskLabel()
    {
        // Three different task labels, one memory type. This is the whole point: the aggregate hides
        // that episodic is a handful of questions carrying its own, very different, score.
        var telemetry = new[]
        {
            Q(1, "a", "single-session-user", present: true),
            Q(2, "b", "multi-session", present: true),
            Q(3, "c", "single-session-preference", present: true),
            Q(4, "d", "single-session-assistant", present: true),
        };
        var correct = new Dictionary<string, bool> { ["a"] = true, ["b"] = true, ["c"] = false, ["d"] = false };

        var rows = LongMemEvalTypedBreakdown.Summarise(telemetry, correct);

        rows.Should().Contain(r => r.MemoryType == "semantic" && r.Questions == 3 && r.Correct == 2);
        rows.Should().Contain(r => r.MemoryType == "episodic" && r.Questions == 1 && r.Correct == 0);
    }

    [Fact]
    public void FailuresAreSplitIntoExtractionAndRetrieval()
    {
        // The split that redirected this whole track: a failure whose answer was never STORED needs a
        // different fix from one whose answer was stored and not surfaced.
        var telemetry = new[]
        {
            Q(1, "stored-but-missed", "single-session-user", present: true),
            Q(2, "never-stored", "single-session-user", present: false),
        };
        var correct = new Dictionary<string, bool> { ["stored-but-missed"] = false, ["never-stored"] = false };

        var row = LongMemEvalTypedBreakdown.Summarise(telemetry, correct)
            .Single(r => r.MemoryType == "semantic");

        row.RetrievalFailures.Should().Be(1);
        row.ExtractionFailures.Should().Be(1);
    }

    [Fact]
    public void AnUncheckableAnswerIsCountedApartFromAnAbsentOne()
    {
        // A derived answer -- "17 fish", "$750" -- is computed from stored evidence and was never itself
        // stored, so overlap cannot find it. Folding that into "extraction failure" would blame
        // extraction for an answer the model got right, which is exactly what it did before.
        var telemetry = new[] { Q(1, "derived", "multi-session", present: false, checkable: false) };
        var correct = new Dictionary<string, bool> { ["derived"] = false };

        var row = LongMemEvalTypedBreakdown.Summarise(telemetry, correct).Single();

        row.Unattributable.Should().Be(1);
        row.ExtractionFailures.Should().Be(0);
    }

    [Fact]
    public void AbstentionQuestionsLandUnderMetaMemory()
    {
        var telemetry = new[] { Q(1, "q_abs", "single-session-user", present: false) };
        var correct = new Dictionary<string, bool> { ["q_abs"] = true };
        var abstention = new Dictionary<string, bool> { ["q_abs"] = true };

        var rows = LongMemEvalTypedBreakdown.Summarise(telemetry, correct, abstention);

        rows.Should().ContainSingle()
            .Which.MemoryType.Should().Be(LongMemEvalMemoryTypeMap.MetaMemory);
    }

    [Fact]
    public void EveryRowCarriesWhatOneQuestionIsWorth()
    {
        // A per-type subset is small, and a bare percentage over six questions invites exactly the
        // comparison it cannot support. Six questions is 16.7 points each, and the row says so.
        var telemetry = Enumerable.Range(1, 6)
            .Select(i => Q(i, $"q{i}", "single-session-assistant", present: true)).ToArray();
        var correct = telemetry.ToDictionary(t => t.QuestionId!, _ => true);

        var row = LongMemEvalTypedBreakdown.Summarise(telemetry, correct).Single();

        row.PointsPerQuestion.Should().BeApproximately(16.67, 0.01);
        row.Accuracy.Should().Be(1.0);
    }

    [Fact]
    public void AQuestionWithNoJudgementIsExcludedRatherThanCountedWrong()
    {
        // An unjudged question is not a failure. Counting it as one would understate every arm.
        var telemetry = new[] { Q(1, "judged", "single-session-user", present: true), Q(2, "unjudged", "single-session-user", present: true) };
        var correct = new Dictionary<string, bool> { ["judged"] = true };

        var row = LongMemEvalTypedBreakdown.Summarise(telemetry, correct).Single();

        row.Questions.Should().Be(1);
        row.Correct.Should().Be(1);
    }
}
