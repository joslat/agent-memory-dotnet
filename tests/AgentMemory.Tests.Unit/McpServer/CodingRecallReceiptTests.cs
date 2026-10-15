using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Gate;
using AgentMemory.McpHost.Coding;
using FluentAssertions;

#pragma warning disable AMGATE001
namespace AgentMemory.Tests.Unit.McpServer;

public sealed class CodingRecallReceiptTests
{
    private static readonly CodingIdentity Owner = new("owner", "alpha");
    private static readonly RecallResult Empty = new() { Context = new MemoryContext { SessionId = "s", AssembledAtUtc = DateTimeOffset.UnixEpoch } };

    [Fact]
    public void Expiry_does_not_assume_wall_clock_or_concurrent_admission_order()
    {
        var clock = new Clock();
        var store = Store(clock, 3);
        var newer = store.Record(Owner, "s", Empty, TimeSpan.Zero);
        clock.Now -= TimeSpan.FromSeconds(100);
        var older = store.Record(Owner, "other", Empty, TimeSpan.Zero);
        clock.Now += TimeSpan.FromSeconds(20);
        store.Get(Owner, "other", older.RecallId).Should().BeNull();
        store.Get(Owner, "s", newer.RecallId).Should().NotBeNull();
    }

    [Fact]
    public void Capacity_eviction_expiry_and_last_receipt_are_session_scoped()
    {
        var clock = new Clock(); var store = Store(clock, 2);
        var a = store.Record(Owner, "a", Empty, TimeSpan.Zero);
        var b = store.Record(Owner, "b", Empty, TimeSpan.Zero);
        store.Get(Owner, "a").Should().Be(a);
        var c = store.Record(Owner, "a", Empty, TimeSpan.Zero);
        store.Get(Owner, "a", a.RecallId).Should().BeNull();
        store.Get(Owner, "a").Should().Be(c);
        store.Get(Owner, "b").Should().Be(b);
        clock.Now += TimeSpan.FromSeconds(10);
        store.Get(Owner, "a").Should().BeNull();
        store.Feedback(Owner, "b", b.RecallId, CodingRecallFeedback.Useful).Should().BeNull();
    }

    [Fact]
    public void Feedback_is_permission_scoped_idempotent_and_does_not_extend_retention()
    {
        var clock = new Clock(); var store = Store(clock);
        var a = store.Record(Owner, "s", Empty, TimeSpan.Zero);
        store.Invoking(x => x.Feedback(Owner with { CanWrite = false }, "s", a.RecallId, CodingRecallFeedback.Useful))
            .Should().Throw<UnauthorizedAccessException>();
        store.Feedback(Owner with { ProjectId = "beta" }, "s", a.RecallId, CodingRecallFeedback.Useful).Should().BeNull();
        store.Feedback(Owner, "other", a.RecallId, CodingRecallFeedback.Useful).Should().BeNull();
        var first = store.Feedback(Owner, "s", a.RecallId, CodingRecallFeedback.Useful)!;
        clock.Now += TimeSpan.FromSeconds(2);
        store.Feedback(Owner, "s", a.RecallId, CodingRecallFeedback.Useful).Should().BeSameAs(first);
        var changed = store.Feedback(Owner, "s", a.RecallId, CodingRecallFeedback.Stale)!;
        changed.FeedbackAtUtc.Should().Be(clock.Now);
        changed.ExpiresAtUtc.Should().Be(a.ExpiresAtUtc);
        store.Invoking(x => x.Feedback(Owner, "s", a.RecallId, (CodingRecallFeedback)100)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Projection_bounds_ids_and_sources_without_retaining_text_or_arbitrary_metadata()
    {
        var facts = Enumerable.Range(0, 70).Select(i => new Fact
        {
            FactId = "fact-" + i, Subject = "secret-subject", Predicate = "is", Object = "secret-object",
            Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch,
            SourceMessageIds = ["a", "b", "c", "d", "e"], Metadata = new Dictionary<string, object> { ["private"] = "secret-metadata" },
        }).ToArray();
        var result = Empty with { TotalItemsRetrieved = 70, EstimatedTokenCount = 99, Truncated = false,
            Context = Empty.Context with { LatencyBudgetExceeded = true,
                RelevantFacts = new() { Items = facts, Diagnostics = new(true, 100, 70, null, null, 0.3, true) },
                WorkingMemoryBlock = "private-profile", GraphRagContext = "private-graph" } };
        var receipt = Store(new()).Record(Owner, "s", result, TimeSpan.FromMilliseconds(321));
        receipt.Items.Should().HaveCount(64).And.OnlyContain(i => i.SourceMessageIds.Count == 4);
        receipt.IdsTruncated.Should().BeTrue(); receipt.Truncated.Should().BeFalse(); receipt.LatencyBudgetExceeded.Should().BeTrue();
        receipt.HasWorkingMemory.Should().BeTrue(); receipt.HasGraphRagContext.Should().BeTrue();
        receipt.Gate.Outcome.Should().Be("unknown"); receipt.ElapsedMilliseconds.Should().Be(321);
        receipt.Sections.Single(x => x.Section == "relevantFacts").Should().Be(new CodingRecallSection("relevantFacts", 70, true, 100, 70, true));
        JsonSerializer.Serialize(receipt, CodingApi.Json).Should().NotContain("secret-").And.NotContain("private-");
    }

    [Fact]
    public void Gate_projection_never_serializes_provider_exception_or_candidate_text()
    {
        var trace = new MemoryGateTrace(MemoryGateMode.Judge, "floor (fallback)", "secret-provider-exception", 0.5, 9, 2,
            "secret-provider-name", 12, [new("id", "semantic", "secret-candidate", 0.1, false)]);
        var result = Empty with { Context = Empty.Context with { Metadata = new Dictionary<string, object> { [MemoryGateTrace.MetadataKey] = trace } } };
        var receipt = Store(new()).Record(Owner, "s", result, TimeSpan.Zero);
        receipt.Gate.Should().Be(new CodingRecallGate("floor (fallback)", "unspecified", 9, 2, 12));
        JsonSerializer.Serialize(receipt, CodingApi.Json).Should().NotContain("secret-");
    }

    [Fact]
    public async Task Concurrent_sessions_have_distinct_ids_and_no_global_last_receipt()
    {
        var store = Store(new(), 128);
        var receipts = await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(() =>
            store.Record(Owner, "session-" + i, Empty, TimeSpan.Zero))));
        receipts.Select(x => x.RecallId).Should().OnlyHaveUniqueItems();
        for (var i = 0; i < receipts.Length; i++) store.Get(Owner, "session-" + i).Should().Be(receipts[i]);
        store.Get(Owner with { OwnerId = "other" }, "session-0").Should().BeNull();
    }

    [Theory]
    [InlineData(0, 10)] [InlineData(1025, 10)] [InlineData(1, 0)] [InlineData(1, 3601)]
    public void Invalid_bounds_are_rejected(int count, int lifetime) =>
        new CodingRecallDiagnosticsOptions { RetainedReceipts = count, LifetimeSeconds = lifetime }.Invoking(x => x.Validate()).Should().Throw<ArgumentException>();

    private static CodingRecallReceipts Store(Clock clock, int count = 10) => new(
        new() { RetainedReceipts = count, LifetimeSeconds = 10 }, new([]), clock);
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UnixEpoch + TimeSpan.FromHours(1);
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
