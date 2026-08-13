using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Context assembly for the precision sweep (P1).
/// </summary>
/// <remarks>
/// The sweep's whole claim is "recall is pinned at 100% and only noise varies". Both halves of that
/// live in <c>BuildContext</c>: drop one gold message and the sweep measures recall, add distractors
/// non-deterministically and two levels differ by their sample as well as their size. Neither failure
/// produces an error — both produce a clean-looking curve — so they are checked here.
/// </remarks>
public sealed class LongMemEvalContextPrecisionTests
{
    private static LongMemEvalEvidenceQuestion Question(int goldSessions, int otherSessions)
    {
        var messages = new List<LongMemEvalMessageOrigin>();
        var gold = new HashSet<string>(StringComparer.Ordinal);
        var ordinal = 0;

        for (var s = 0; s < goldSessions; s++)
        {
            var sessionId = $"gold-{s}";
            gold.Add(sessionId);
            for (var m = 0; m < 2; m++)
                messages.Add(Message(ordinal++, sessionId, $"gold {s}/{m}"));
        }

        for (var s = 0; s < otherSessions; s++)
        {
            var sessionId = $"other-{s:D2}";
            for (var m = 0; m < 2; m++)
                messages.Add(Message(ordinal++, sessionId, $"noise {s}/{m}"));
        }

        return new LongMemEvalEvidenceQuestion(
            "q-1", "multi-session", "How many?", "How many?", "2", "2023/05/30 (Tue) 12:00",
            IsAbstention: false, gold, AnnotatedGoldTurnCount: goldSessions * 2, messages);
    }

    private static LongMemEvalMessageOrigin Message(int ordinal, string sessionId, string content) =>
        new(ordinal, sessionId, 0, ordinal, "2023/05/30 (Tue) 12:00", "user", content, false, false, false);

    [Fact]
    public void EveryGoldMessageSurvivesAtEveryDistractorCount()
    {
        // THE invariant the sweep rests on. If a gold message can be dropped, the curve measures
        // recall and precision at once and neither number means anything.
        var question = Question(goldSessions: 2, otherSessions: 20);

        foreach (var k in new[] { 0, 1, 5, 20, 99 })
        {
            var context = LongMemEvalContextPrecisionProgram.BuildContext(question, k, seed: 42, out _);

            context.Count(entry => entry.Content.StartsWith("gold", StringComparison.Ordinal))
                .Should().Be(4, "K={0} must not remove gold", k);
        }
    }

    [Fact]
    public void DistractorsActuallyGrowTheContext()
    {
        var question = Question(goldSessions: 1, otherSessions: 10);

        var none = LongMemEvalContextPrecisionProgram.BuildContext(question, 0, 42, out var added0);
        var some = LongMemEvalContextPrecisionProgram.BuildContext(question, 4, 42, out var added4);

        added0.Should().Be(0);
        added4.Should().Be(4);
        some.Count.Should().BeGreaterThan(none.Count);
    }

    [Fact]
    public void TheSelectionIsDeterministicGivenTheSeed()
    {
        // Two levels of a sweep must differ only in HOW MANY distractors they carry. If the sample
        // also changed between runs, a difference between K=3 and K=10 could be which sessions were
        // drawn rather than how many.
        var question = Question(goldSessions: 1, otherSessions: 15);

        var first = LongMemEvalContextPrecisionProgram.BuildContext(question, 5, 42, out _);
        var second = LongMemEvalContextPrecisionProgram.BuildContext(question, 5, 42, out _);

        second.Select(entry => entry.Content).Should().Equal(first.Select(entry => entry.Content));
    }

    [Fact]
    public void MessageOrderIsPreservedRatherThanGoldFirst()
    {
        // Grouping gold at the top would hand the model a positional cue no retriever provides, and
        // the sweep would measure how well it reads an ordered list instead of how it copes with noise.
        var question = Question(goldSessions: 1, otherSessions: 6);

        var context = LongMemEvalContextPrecisionProgram.BuildContext(question, 6, 42, out _);

        var goldPositions = context
            .Select((entry, index) => (entry, index))
            .Where(pair => pair.entry.Content.StartsWith("gold", StringComparison.Ordinal))
            .Select(pair => pair.index)
            .ToList();

        goldPositions.Should().Equal([0, 1]);
        context.Should().HaveCount(14);
    }

    [Fact]
    public void AskingForMoreDistractorsThanExistTakesWhatThereIs()
    {
        // Reported through the out-parameter rather than thrown: a question whose haystack is small is
        // a fact about the dataset, and the sweep's witness needs to see that K produced fewer than
        // requested rather than have the level fail.
        var question = Question(goldSessions: 1, otherSessions: 3);

        LongMemEvalContextPrecisionProgram.BuildContext(question, 50, 42, out var added);

        added.Should().Be(3);
    }

    [Fact]
    public void AQuestionWithNoDistractorsAvailableReportsZero()
    {
        // The case the void witness watches for: if every question is like this, K>0 is the gold-only
        // level wearing a different label.
        var question = Question(goldSessions: 2, otherSessions: 0);

        var context = LongMemEvalContextPrecisionProgram.BuildContext(question, 10, 42, out var added);

        added.Should().Be(0);
        context.Should().HaveCount(4);
    }
}
