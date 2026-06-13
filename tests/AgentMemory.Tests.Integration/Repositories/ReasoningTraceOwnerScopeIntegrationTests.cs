using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Owner-isolation guarantees for ReasoningTrace recall (R1 / IC1). A scoped task-vector search must
/// return only the owner's (plus shared/global) traces, never another user's.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ReasoningTraceOwnerScopeIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jReasoningTraceRepository _repo;

    private static readonly float[] Embedding = [0.3f, 0.1f, 0.4f, 0.2f];

    public ReasoningTraceOwnerScopeIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jReasoningTraceRepository(fixture.TransactionRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private ReasoningTrace NewTrace(string task, string? ownerId) => new()
    {
        TraceId = $"trace-{Guid.NewGuid():N}",
        SessionId = $"session-{Guid.NewGuid():N}",
        OwnerId = ownerId,
        Task = task,
        TaskEmbedding = Embedding,
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ScopedTaskSearch_ReturnsOwnerAndShared_NotOtherUsers()
    {
        await _repo.AddAsync(NewTrace("alice plans a trip", ownerId: "alice"));
        await _repo.AddAsync(NewTrace("bob debugs a service", ownerId: "bob"));
        await _repo.AddAsync(NewTrace("org onboarding runbook", ownerId: null)); // shared

        var results = await _repo.SearchByTaskVectorAsync(
            Embedding, successFilter: null, limit: 10, minScore: 0.0, scope: MemoryScope.For("alice"));

        var owners = results.Select(r => r.Trace.OwnerId).ToList();
        owners.Should().Contain("alice");
        owners.Should().Contain((string?)null);   // shared visible
        owners.Should().NotContain("bob");          // other user's traces never leak
    }

    [Fact]
    public async Task ScopedTaskSearch_IncludeSharedFalse_ReturnsOwnerOnly()
    {
        await _repo.AddAsync(NewTrace("alice plans a trip", ownerId: "alice"));
        await _repo.AddAsync(NewTrace("org onboarding runbook", ownerId: null));

        var results = await _repo.SearchByTaskVectorAsync(
            Embedding, successFilter: null, limit: 10, minScore: 0.0,
            scope: MemoryScope.For("alice", includeShared: false));

        results.Should().NotBeEmpty();
        results.Select(r => r.Trace.OwnerId).Should().AllBe("alice");
    }

    [Fact]
    public async Task UnscopedTaskSearch_ReturnsEveryone()
    {
        await _repo.AddAsync(NewTrace("alice plans a trip", ownerId: "alice"));
        await _repo.AddAsync(NewTrace("bob debugs a service", ownerId: "bob"));

        var results = await _repo.SearchByTaskVectorAsync(Embedding, successFilter: null, limit: 10);

        results.Select(r => r.Trace.OwnerId).Should().Contain(new[] { "alice", "bob" });
    }

    [Fact]
    public async Task OwnerId_RoundTrips_OnWriteAndRead()
    {
        var trace = NewTrace("alice plans a trip", ownerId: "alice");
        await _repo.AddAsync(trace);

        var read = await _repo.GetByIdAsync(trace.TraceId);

        read.Should().NotBeNull();
        read!.OwnerId.Should().Be("alice");
    }

    // ── ListBySession owner-scoping (R2) ─────────────────────────────────
    // A session_id is not a private handle (it can be shared/guessable), so a multi-row list keyed by it
    // must be owner-filtered when scoped — unlike a by-id handle.

    [Fact]
    public async Task ScopedListBySession_ReturnsOwnerAndShared_NotOtherUsers()
    {
        const string session = "shared-session-r2";
        await _repo.AddAsync(InSession(session, "alice plans", "alice"));
        await _repo.AddAsync(InSession(session, "bob debugs", "bob"));
        await _repo.AddAsync(InSession(session, "org runbook", null)); // shared

        var scoped = await _repo.ListBySessionAsync(session, limit: 10, scope: MemoryScope.For("alice"));

        var owners = scoped.Select(t => t.OwnerId).ToList();
        owners.Should().Contain("alice");
        owners.Should().Contain((string?)null);                 // shared visible
        owners.Should().NotContain("bob", "another owner's traces in the same session must never leak");
    }

    [Fact]
    public async Task ScopedListBySession_IncludeSharedFalse_ReturnsOwnerOnly()
    {
        const string session = "shared-session-r2b";
        await _repo.AddAsync(InSession(session, "alice plans", "alice"));
        await _repo.AddAsync(InSession(session, "org runbook", null));

        var scoped = await _repo.ListBySessionAsync(
            session, limit: 10, scope: MemoryScope.For("alice", includeShared: false));

        scoped.Should().NotBeEmpty();
        scoped.Select(t => t.OwnerId).Should().AllBe("alice");
    }

    [Fact]
    public async Task UnscopedListBySession_ReturnsEveryone()
    {
        const string session = "shared-session-r2c";
        await _repo.AddAsync(InSession(session, "alice plans", "alice"));
        await _repo.AddAsync(InSession(session, "bob debugs", "bob"));

        var all = await _repo.ListBySessionAsync(session, limit: 10);

        all.Select(t => t.OwnerId).Should().Contain(new[] { "alice", "bob" });
    }

    private ReasoningTrace InSession(string sessionId, string task, string? ownerId) => new()
    {
        TraceId = $"trace-{Guid.NewGuid():N}",
        SessionId = sessionId,
        OwnerId = ownerId,
        Task = task,
        TaskEmbedding = Embedding,
        StartedAtUtc = DateTimeOffset.UtcNow
    };
}
