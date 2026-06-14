using FluentAssertions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services.Budgeting;

namespace AgentMemory.Tests.Unit.Services.Budgeting;

/// <summary>
/// S9 — direct unit tests for the individual <see cref="ITruncationStrategy"/> implementations extracted
/// from <c>MemoryContextAssembler</c>. These exercise each policy in isolation; the assembler-level budget
/// behaviour is also covered end-to-end by <c>MemoryContextAssemblerTests</c> (the regression guarantee
/// that this extraction is behaviour-preserving).
/// </summary>
public sealed class TruncationStrategyTests
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static Message Msg(string id, int contentLen, DateTimeOffset ts) => new()
    {
        MessageId = id,
        ConversationId = "c",
        SessionId = "s",
        Role = "user",
        Content = new string('x', contentLen),
        TimestampUtc = ts
    };

    private static TruncationInput Input(
        int maxChars,
        IReadOnlyList<Message>? recent = null,
        IReadOnlyList<Message>? relevant = null,
        string? graphRag = null)
    {
        recent ??= Array.Empty<Message>();
        relevant ??= Array.Empty<Message>();
        int total = ContextBudgetEstimator.EstimateChars(recent)
            + ContextBudgetEstimator.EstimateChars(relevant)
            + (graphRag?.Length ?? 0);
        return new TruncationInput(
            maxChars, total, recent, relevant,
            Array.Empty<Entity>(), Array.Empty<Preference>(), Array.Empty<Fact>(),
            Array.Empty<ReasoningTrace>(), graphRag);
    }

    // ── Strategy ⇒ enum mapping ──────────────────────────────────────────────

    [Fact]
    public void EachStrategy_MapsToItsEnumValue()
    {
        new OldestFirstTruncationStrategy().Strategy.Should().Be(TruncationStrategy.OldestFirst);
        new LowestScoreFirstTruncationStrategy().Strategy.Should().Be(TruncationStrategy.LowestScoreFirst);
        new ProportionalTruncationStrategy().Strategy.Should().Be(TruncationStrategy.Proportional);
        new FailTruncationStrategy().Strategy.Should().Be(TruncationStrategy.Fail);
    }

    // ── Fail ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Fail_Throws_ContextBudgetExceeded_WithTotals()
    {
        var input = Input(maxChars: 5, recent: new[] { Msg("a", 10, T0) });

        var act = () => new FailTruncationStrategy().Truncate(input);

        act.Should().Throw<MemoryException>()
            .Where(e => e.Code == MemoryErrorCodes.ContextBudgetExceeded)
            .Where(e => e.Metadata.ContainsKey("totalChars") && e.Metadata.ContainsKey("maxChars"));
    }

    // ── OldestFirst ──────────────────────────────────────────────────────────

    [Fact]
    public void OldestFirst_RemovesOldestItemFirst()
    {
        var older = Msg("older", 10, T0);
        var newer = Msg("newer", 10, T0.AddHours(1));
        // 20 chars total, budget 12 ⇒ exactly one item must be dropped.
        var result = new OldestFirstTruncationStrategy().Truncate(Input(maxChars: 12, recent: new[] { older, newer }));

        result.Recent.Should().ContainSingle().Which.MessageId.Should().Be("newer");
    }

    // ── LowestScoreFirst ─────────────────────────────────────────────────────

    [Fact]
    public void LowestScoreFirst_RemovesTrailing_LowestRelevance_First()
    {
        // best-score-first ordering: index 0 = best (scoreRank 1.0), index 1 = worst (scoreRank 0.0).
        // Give the BEST item the OLDER timestamp so an oldest-first policy would behave differently —
        // this isolates the score-driven choice.
        var best = Msg("best", 10, T0);              // oldest, but highest relevance
        var worst = Msg("worst", 10, T0.AddHours(1)); // newest, but lowest relevance
        var result = new LowestScoreFirstTruncationStrategy().Truncate(Input(maxChars: 12, relevant: new[] { best, worst }));

        result.Relevant.Should().ContainSingle().Which.MessageId.Should().Be("best");
    }

    // ── Proportional ─────────────────────────────────────────────────────────

    [Fact]
    public void Proportional_ShrinksSection_AndGraphRag_ByRatio()
    {
        // recent: 4 × 10 chars = 40; graphRag 20 chars; total 60; budget 30 ⇒ ratio 0.5.
        var recent = new[] { Msg("0", 10, T0), Msg("1", 10, T0), Msg("2", 10, T0), Msg("3", 10, T0) };
        var input = Input(maxChars: 30, recent: recent, graphRag: new string('g', 20));

        var result = new ProportionalTruncationStrategy().Truncate(input);

        // target per section = 40 * 0.5 = 20 chars ⇒ keep 2 messages (10+10).
        result.Recent.Should().HaveCount(2);
        // graphRag trimmed to floor(20 * 0.5) = 10 chars.
        result.GraphRag.Should().HaveLength(10);
    }

    [Fact]
    public void Proportional_NullGraphRag_StaysNull()
    {
        var input = Input(maxChars: 10, recent: new[] { Msg("0", 10, T0), Msg("1", 10, T0) }, graphRag: null);

        var result = new ProportionalTruncationStrategy().Truncate(input);

        result.GraphRag.Should().BeNull();
    }
}
