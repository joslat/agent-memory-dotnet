using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G3B.3. A per-source-session cap on how much of the answer budget one session may occupy.
/// Measured target: `gpt4_7abb270c` needs six gold sessions and gets five, because two sessions take
/// 14 of 30 slots while the sixth gold session gets none — the item is in the candidate pool, it just
/// never receives a slot.
/// </summary>
public sealed class LongMemEvalSessionDiversityTests
{
    [Fact]
    public void TheCapFreesSlotsForASessionThatWouldOtherwiseBeCrowdedOut()
    {
        // Ranked pool: session A monopolises the top, session F sits below the budget line.
        var section = Section(
            Ranked("a1", "A"), Ranked("a2", "A"), Ranked("a3", "A"), Ranked("a4", "A"),
            Ranked("b1", "B"), Ranked("f1", "F"));

        var capped = LongMemEvalRecallBudget.SelectRealSourceTurns(
            section, Origins(section), finalCap: 5, maxPerSession: 2);

        // f1 would have been crowded out entirely without the cap; that is the whole point.
        Assert.Contains(capped.Items, m => m.MessageId == "f1");
        // A's share shrinks, but not necessarily to the cap: once the capped pass leaves slots
        // spare, refill returns skipped items so the budget stays exactly full (M1).
        Assert.True(
            capped.Items.Count(m => m.MessageId.StartsWith('a')) < 4,
            "the cap must reallocate at least one slot away from the monopolising session");
        Assert.Equal(5, capped.Items.Count);
    }

    [Fact]
    public void TheBudgetIsStillFilledExactlyWhenTheCapWouldUnderfillIt()
    {
        // Refill is what stops the cap from silently shrinking the context: after the capped pass
        // only 2 of 4 slots are taken, so the skipped items come back, uncapped.
        var section = Section(
            Ranked("a1", "A"), Ranked("a2", "A"), Ranked("a3", "A"), Ranked("a4", "A"),
            Ranked("b1", "B"));

        var capped = LongMemEvalRecallBudget.SelectRealSourceTurns(
            section, Origins(section), finalCap: 4, maxPerSession: 1);

        Assert.Equal(4, capped.Items.Count);
    }

    [Fact]
    public void RetrievalOrderIsPreservedAmongTheSurvivors()
    {
        var section = Section(
            Ranked("a1", "A"), Ranked("b1", "B"), Ranked("a2", "A"), Ranked("c1", "C"));

        var capped = LongMemEvalRecallBudget.SelectRealSourceTurns(
            section, Origins(section), finalCap: 3, maxPerSession: 1);

        Assert.Equal(["a1", "b1", "c1"], capped.Items.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void ACapOfZeroLeavesSelectionExactlyAsItWas()
    {
        // The accepted control must be bit-identical with the feature off.
        var section = Section(
            Ranked("a1", "A"), Ranked("a2", "A"), Ranked("a3", "A"), Ranked("b1", "B"));

        var uncapped = LongMemEvalRecallBudget.SelectRealSourceTurns(
            section, Origins(section), finalCap: 3, maxPerSession: 0);

        Assert.Equal(["a1", "a2", "a3"], uncapped.Items.Select(m => m.MessageId).ToArray());
    }

    [Fact]
    public void AnItemWithNoKnownOriginIsNeverCappedAway()
    {
        // "Keep what we cannot classify" — dropping unknowns would silently shrink the budget.
        var section = Section(Ranked("a1", "A"), Ranked("a2", "A"), Ranked("x1", null), Ranked("x2", null));

        var capped = LongMemEvalRecallBudget.SelectRealSourceTurns(
            section, Origins(section), finalCap: 4, maxPerSession: 1);

        Assert.Contains(capped.Items, m => m.MessageId == "x1");
        Assert.Contains(capped.Items, m => m.MessageId == "x2");
    }

    private static (string Id, string? Session) Ranked(string id, string? session) => (id, session);

    private static (string Id, string? Session)[] _lastItems = [];

    private static MemoryContextSection<Message> Section(params (string Id, string? Session)[] items)
    {
        _lastItems = items;
        return new MemoryContextSection<Message>
        {
            Items = items.Select(item => new Message
            {
                MessageId = item.Id,
                SessionId = "s",
                ConversationId = "s",
                Role = "user",
                Content = item.Id,
                TimestampUtc = DateTimeOffset.UnixEpoch
            }).ToArray(),
            RankedItems = items.Select((item, index) => new MemoryContextRankedItem(
                item.Id, 1.0 - index * 0.01, index + 1, index + 1)).ToArray()
        };
    }

    private static IReadOnlyDictionary<string, LongMemEvalMessageOrigin> Origins(
        MemoryContextSection<Message> section) =>
        _lastItems
            .Where(item => item.Session is not null)
            .ToDictionary(
                item => item.Id,
                item => new LongMemEvalMessageOrigin(
                    0, item.Session!, 0, 0, "2023/05/20 (Sat) 10:19", "user",
                    item.Id, false, false, false),
                StringComparer.Ordinal);
}
