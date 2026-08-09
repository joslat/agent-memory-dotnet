using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// G3B.1. AgentEval's formatter injects per-session boilerplate that our bridge stores as ordinary
/// embedded messages. In the accepted r8 run those artifacts were 240 of 300 final items, and both
/// questions reported as retrieval failures returned 30 of 30 — the budget was consumed before a real
/// source turn could rank. Selection must be able to drop them without a second query.
/// </summary>
public sealed class LongMemEvalSyntheticExclusionTests
{
    private const int FinalCap = 30;

    /// <summary>Ranks 1-30 are formatter artifacts; ranks 31-60 are real source turns.</summary>
    private static (MemoryContextSection<Message> Section,
        Dictionary<string, LongMemEvalMessageOrigin> Origins) Candidates()
    {
        var items = new List<Message>();
        var ranked = new List<MemoryContextRankedItem>();
        var origins = new Dictionary<string, LongMemEvalMessageOrigin>(StringComparer.Ordinal);

        for (var index = 0; index < 60; index++)
        {
            var synthetic = index < 30;
            var id = $"m-{index:D2}";
            items.Add(new Message
            {
                MessageId = id,
                SessionId = "evaluation-session",
                ConversationId = "evaluation-session",
                Role = "user",
                Content = synthetic ? "--- Session 2026-01-01 ---" : $"real source turn {index}",
                TimestampUtc = DateTimeOffset.UnixEpoch.AddSeconds(index)
            });
            ranked.Add(new MemoryContextRankedItem(
                id, Score: 1d - index / 100d, RetrievalRank: index + 1, ContextRank: index + 1));
            origins[id] = new LongMemEvalMessageOrigin(
                MessageOrdinal: index,
                SourceSessionId: $"s-{index}",
                SourceSessionOrdinal: index,
                SourceTurnOrdinal: synthetic ? null : index,
                SourceTimestamp: "2026-01-01T00:00:00Z",
                Role: "user",
                FormattedContent: items[^1].Content,
                IsSyntheticBoundary: synthetic,
                IsSyntheticFormatterPadding: false,
                HasAnswer: false);
        }

        return (new MemoryContextSection<Message> { Items = items, RankedItems = ranked }, origins);
    }

    /// <summary>
    /// The control, and the reason this work exists: with the flag off, the unfiltered top-30 is
    /// entirely formatter boilerplate and contains no real source turn at all. This is the r8
    /// 30-of-30 observation reproduced deterministically.
    /// </summary>
    [Fact]
    public void UnfilteredSelection_ReturnsOnlyFormatterArtifacts()
    {
        var (section, origins) = Candidates();

        var unfiltered = section.RankedItems
            .OrderBy(item => item.ContextRank)
            .Take(FinalCap)
            .Select(item => item.ItemId)
            .ToArray();

        unfiltered.Should().OnlyContain(id => origins[id].IsSyntheticBoundary);
        unfiltered.Should().NotContain(id => origins[id].SyntheticBoundaryIsFalse(),
            "the budget is consumed before a real source turn can rank");
    }

    [Fact]
    public void SelectRealSourceTurns_DropsFormatterArtifactsAndFillsTheBudget()
    {
        var (section, origins) = Candidates();

        var selected = LongMemEvalRecallBudget.SelectRealSourceTurns(section, origins, FinalCap);

        selected.Items.Should().HaveCount(FinalCap, "the over-fetch must still fill the budget");
        selected.Items.Select(m => m.MessageId)
            .Should().OnlyContain(id => origins[id].SyntheticBoundaryIsFalse());
        selected.Items.Select(m => m.MessageId).Should().Equal(
            Enumerable.Range(30, 30).Select(index => $"m-{index:D2}"),
            "the surviving real turns must keep the provider's retrieval order");
    }

    [Fact]
    public void SelectRealSourceTurns_PreservesRetrievalRankAndRenumbersContextRank()
    {
        var (section, origins) = Candidates();

        var selected = LongMemEvalRecallBudget.SelectRealSourceTurns(section, origins, FinalCap);

        selected.RankedItems.Select(item => item.ContextRank)
            .Should().Equal(Enumerable.Range(1, 30), "context rank is renumbered over survivors");
        selected.RankedItems.Select(item => item.RetrievalRank)
            .Should().Equal(Enumerable.Range(31, 30),
                "the provider's own rank must survive so the candidate ceiling stays reportable");
        selected.RankedItems.Should().HaveCount(selected.Items.Count);
    }

    [Fact]
    public void SelectRealSourceTurns_KeepsItemsWithNoKnownOrigin()
    {
        var (section, origins) = Candidates();
        origins.Remove("m-00");

        var selected = LongMemEvalRecallBudget.SelectRealSourceTurns(section, origins, FinalCap);

        selected.Items.Select(m => m.MessageId).Should().Contain("m-00",
            "dropping what cannot be classified would silently shrink the budget");
    }

    [Fact]
    public void SelectRealSourceTurns_WithoutDiagnostics_StillFiltersInItemOrder()
    {
        var (section, origins) = Candidates();
        var noDiagnostics = section with { RankedItems = [] };

        var selected = LongMemEvalRecallBudget.SelectRealSourceTurns(noDiagnostics, origins, FinalCap);

        selected.Items.Should().HaveCount(FinalCap);
        selected.Items.Select(m => m.MessageId).Should().Equal(
            Enumerable.Range(30, 30).Select(index => $"m-{index:D2}"));
    }
}

internal static class SyntheticExclusionTestExtensions
{
    internal static bool SyntheticBoundaryIsFalse(this LongMemEvalMessageOrigin origin) =>
        !origin.IsSyntheticBoundary && !origin.IsSyntheticFormatterPadding;
}
