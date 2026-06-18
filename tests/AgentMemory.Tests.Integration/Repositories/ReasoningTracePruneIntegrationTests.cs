using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Integration coverage for the per-session retention prune (H): keep the newest N traces, delete older
/// ones (with their child steps), and honor R1 owner scoping.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ReasoningTracePruneIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jReasoningTraceRepository _traceRepo;
    private readonly Neo4jReasoningStepRepository _stepRepo;

    public ReasoningTracePruneIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _traceRepo = new Neo4jReasoningTraceRepository(
            fixture.TransactionRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance);
        _stepRepo = new Neo4jReasoningStepRepository(
            fixture.TransactionRunner, NullLogger<Neo4jReasoningStepRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private ReasoningTrace MakeTrace(string sessionId, int dayOffset, string? ownerId = null) => new()
    {
        TraceId = $"trace-{Guid.NewGuid():N}",
        SessionId = sessionId,
        OwnerId = ownerId,
        Task = $"Task day {dayOffset}",
        StartedAtUtc = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero).AddDays(dayOffset)
    };

    [Fact]
    public async Task PruneSessionTracesAsync_KeepsNewest_DeletesOlder_ReturnsCount()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var traces = new List<ReasoningTrace>();
        for (var day = 0; day < 5; day++)
        {
            var t = MakeTrace(sessionId, day);
            await _traceRepo.AddAsync(t);
            traces.Add(t);
        }

        var pruned = await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 3);

        pruned.Should().Be(2);
        var remaining = await _traceRepo.ListBySessionAsync(sessionId, limit: 100);
        remaining.Select(t => t.TraceId).Should().BeEquivalentTo(
            new[] { traces[4].TraceId, traces[3].TraceId, traces[2].TraceId },
            "the 3 newest (by started_at) are retained");
    }

    [Fact]
    public async Task PruneSessionTracesAsync_DeletesChildStepsOfPrunedTraces()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var older = MakeTrace(sessionId, 0);
        var newer = MakeTrace(sessionId, 1);
        await _traceRepo.AddAsync(older);
        await _traceRepo.AddAsync(newer);

        var step = new ReasoningStep
        {
            StepId = $"step-{Guid.NewGuid():N}",
            TraceId = older.TraceId,
            StepNumber = 1,
            Thought = "to be pruned"
        };
        await _stepRepo.AddAsync(step);

        var pruned = await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 1);

        pruned.Should().Be(1);
        (await _traceRepo.GetByIdAsync(older.TraceId)).Should().BeNull("older trace was pruned");
        (await _traceRepo.GetByIdAsync(newer.TraceId)).Should().NotBeNull("newest trace is retained");

        var orphanStepCount = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (s:ReasoningStep {id: $sid}) RETURN count(s) AS c", new { sid = step.StepId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });
        orphanStepCount.Should().Be(0, "the pruned trace's child step must be deleted, not orphaned");
    }

    [Fact]
    public async Task PruneSessionTracesAsync_NothingToPrune_ReturnsZero_AndKeepsAll()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        await _traceRepo.AddAsync(MakeTrace(sessionId, 0));
        await _traceRepo.AddAsync(MakeTrace(sessionId, 1));

        var pruned = await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 5);

        pruned.Should().Be(0);
        (await _traceRepo.ListBySessionAsync(sessionId, limit: 100)).Should().HaveCount(2);
    }

    [Fact]
    public async Task PruneSessionTracesAsync_OwnerScopedOwnOnly_DoesNotEvictOtherOwnersOrShared()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        // Alice has 3 traces; Bob has 2; one shared (owner null). Same session id (a guessable handle).
        var alice = new List<ReasoningTrace>();
        for (var day = 0; day < 3; day++)
        {
            var t = MakeTrace(sessionId, day, ownerId: "alice");
            await _traceRepo.AddAsync(t);
            alice.Add(t);
        }
        var bob1 = MakeTrace(sessionId, 0, ownerId: "bob");
        var bob2 = MakeTrace(sessionId, 1, ownerId: "bob");
        var shared = MakeTrace(sessionId, 0, ownerId: null);
        await _traceRepo.AddAsync(bob1);
        await _traceRepo.AddAsync(bob2);
        await _traceRepo.AddAsync(shared);

        // Prune Alice's own bucket to keep 1 → 2 of Alice's pruned; Bob's + shared untouched.
        var pruned = await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 1, ownerId: "alice");

        pruned.Should().Be(2);

        var aliceRemaining = await _traceRepo.ListBySessionAsync(sessionId, limit: 100, scope: MemoryScope.For("alice", includeShared: false));
        aliceRemaining.Should().ContainSingle().Which.TraceId.Should().Be(alice[2].TraceId, "only Alice's newest survives");

        var bobRemaining = await _traceRepo.ListBySessionAsync(sessionId, limit: 100, scope: MemoryScope.For("bob", includeShared: false));
        bobRemaining.Select(t => t.TraceId).Should().BeEquivalentTo(new[] { bob1.TraceId, bob2.TraceId },
            "Bob's traces are never touched by Alice's prune");

        (await _traceRepo.GetByIdAsync(shared.TraceId)).Should().NotBeNull("shared trace is never evicted by an own-only prune");
    }

    [Fact]
    public async Task PruneSessionTracesAsync_NullOwner_PrunesSharedBucketOnly_NeverEvictsOwnedTraces()
    {
        // R5 regression of #40: a null-owner (shared/MCP) prune must NOT cross-evict owner-tagged traces in
        // the same (guessable) session. Before the fix, null owner collapsed to "all owners" and the
        // destructive prune DETACH-DELETEd Bob's and Alice's traces.
        var sessionId = $"session-{Guid.NewGuid():N}";
        var shared = new List<ReasoningTrace>();
        for (var day = 0; day < 3; day++)
        {
            var t = MakeTrace(sessionId, day, ownerId: null);
            await _traceRepo.AddAsync(t);
            shared.Add(t);
        }
        var bob1 = MakeTrace(sessionId, 0, ownerId: "bob");
        var bob2 = MakeTrace(sessionId, 1, ownerId: "bob");
        var alice1 = MakeTrace(sessionId, 0, ownerId: "alice");
        await _traceRepo.AddAsync(bob1);
        await _traceRepo.AddAsync(bob2);
        await _traceRepo.AddAsync(alice1);

        // Prune the shared bucket to keep 1 → 2 shared pruned; every OWNED trace must survive.
        var pruned = await _traceRepo.PruneSessionTracesAsync(sessionId, maxToKeep: 1, ownerId: null);

        pruned.Should().Be(2);
        (await _traceRepo.GetByIdAsync(shared[2].TraceId)).Should().NotBeNull("the newest shared trace survives");
        foreach (var owned in new[] { bob1.TraceId, bob2.TraceId, alice1.TraceId })
            (await _traceRepo.GetByIdAsync(owned)).Should().NotBeNull(
                "a shared-bucket prune must never evict another owner's traces (the #40 cross-eviction)");
    }
}
