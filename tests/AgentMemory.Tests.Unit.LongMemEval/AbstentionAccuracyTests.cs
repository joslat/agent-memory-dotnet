using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Meta-memory: how well the agent declines to answer what memory does not hold.
/// </summary>
/// <remarks>
/// <para>
/// This exists because two different things share the word "absent", and the difference is easy to
/// miss. The sufficiency AUC's <c>absentCount</c> is a ground-truth <b>input</b> — the label
/// retrieval confidence is ordered against — and it is identical across arms by construction.
/// Abstention <i>accuracy</i> is an <b>outcome</b>: whether the agent behaved correctly on those
/// questions. Reporting only the first invites reading a class balance as a result.
/// </para>
/// <para>
/// It is worth its own line because the project's taxonomy names abstention questions as the only
/// place this dataset scores meta-memory at all, and across 52 recorded runs before typed sampling
/// shipped, none had ever been drawn.
/// </para>
/// </remarks>
public sealed class AbstentionAccuracyTests
{
    private static LongMemEvalQuestionTelemetry Question(int number, string id, bool isAbstention) =>
        new(number, MessagesStored: 10, ItemsRetrieved: 5, RecallTruncated: false, Status: "ok")
        {
            QuestionId = id,
            IsAbstention = isAbstention,
        };

    private static Func<string?, bool?> Verdicts(params (string Id, bool? Correct)[] verdicts)
    {
        var map = verdicts.ToDictionary(v => v.Id, v => v.Correct, StringComparer.Ordinal);
        return id => id is not null && map.TryGetValue(id, out var v) ? v : null;
    }

    [Fact]
    public void TheTwoHalvesAreScoredSeparately()
    {
        // The whole point: an aggregate hides whether the system is good at knowing what it does not
        // know, which is a different capability from retrieving what it does.
        var telemetry = new[]
        {
            Question(1, "abs-1", isAbstention: true),
            Question(2, "abs-2", isAbstention: true),
            Question(3, "ord-1", isAbstention: false),
        };

        var scored = LongMemEvalAbstentionAccuracy.Score(telemetry, Verdicts(
            ("abs-1", true), ("abs-2", false), ("ord-1", true)));

        scored.AbstentionCorrect.Should().Be(1);
        scored.AbstentionTotal.Should().Be(2);
        scored.OrdinaryCorrect.Should().Be(1);
        scored.OrdinaryTotal.Should().Be(1);
    }

    [Fact]
    public void AnsweringWhenItShouldHaveAbstainedIsNamedForTheBehaviour()
    {
        // Not "incorrect": this specific wrong answer is the interesting one, because the agent
        // asserted something memory did not hold. That is the failure a memory system is least able
        // to detect in itself.
        var telemetry = new[]
        {
            Question(1, "abs-1", isAbstention: true),
            Question(2, "abs-2", isAbstention: true),
        };

        var scored = LongMemEvalAbstentionAccuracy.Score(telemetry, Verdicts(
            ("abs-1", true), ("abs-2", false)));

        scored.AnsweredWhenItShouldHaveAbstained.Should().Be(1);
    }

    [Fact]
    public void NoAbstentionQuestionsMeansNotMeasuredRatherThanZero()
    {
        // Before typed sampling this was every run. A rate of 0 would read as "it never abstains
        // correctly"; null reads as "it was never asked to", which is the truth.
        var scored = LongMemEvalAbstentionAccuracy.Score(
            [Question(1, "ord-1", isAbstention: false)], Verdicts(("ord-1", true)));

        scored.AbstentionAccuracy.Should().BeNull();
        scored.AbstentionTotal.Should().Be(0);
        scored.OrdinaryAccuracy.Should().Be(1.0);
    }

    [Fact]
    public void AnUnjudgedQuestionLeavesBothDenominatorsAlone()
    {
        // Counting it wrong would understate every rate by however many the judge could not parse --
        // and judge-parse failures are a known, recurring class here.
        var telemetry = new[]
        {
            Question(1, "abs-1", isAbstention: true),
            Question(2, "abs-2", isAbstention: true),
        };

        var scored = LongMemEvalAbstentionAccuracy.Score(telemetry, Verdicts(
            ("abs-1", true), ("abs-2", null)));

        scored.AbstentionTotal.Should().Be(1, "an unjudged question measured nothing");
        scored.AbstentionAccuracy.Should().Be(1.0);
    }

    [Fact]
    public void TheMeasuredRunReproduces()
    {
        // The 50q abstention-enriched run of 2026-08-12: 18 of 20 abstention questions correct in both
        // arms, against 25/30 and 26/30 ordinary. Pinned as a shape check on the scorer -- if this
        // ever computes 20/20 or 0/20 from the same inputs, the scorer changed, not the system.
        var telemetry = Enumerable.Range(1, 20)
            .Select(i => Question(i, $"abs-{i}", isAbstention: true))
            .Concat(Enumerable.Range(21, 30).Select(i => Question(i, $"ord-{i}", isAbstention: false)))
            .ToArray();

        var scored = LongMemEvalAbstentionAccuracy.Score(telemetry, id =>
            id is null ? null
            : id.StartsWith("abs-", StringComparison.Ordinal) ? id is not ("abs-1" or "abs-2")
            : id is not ("ord-21" or "ord-22" or "ord-23" or "ord-24" or "ord-25"));

        scored.AbstentionAccuracy.Should().BeApproximately(0.90, 1e-9);
        scored.OrdinaryAccuracy.Should().BeApproximately(25.0 / 30.0, 1e-9);
    }
}
