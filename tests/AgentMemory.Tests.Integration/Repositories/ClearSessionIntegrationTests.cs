using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Live-DB coverage for the DELETE_SESSION_DATA parity feature (#51). Verifies that
/// <see cref="ShortTermMemoryService.ClearSessionAsync"/> removes every session-scoped node type
/// — Message, Conversation, ReasoningTrace, and the ReasoningTrace's HAS_STEP children — for the
/// target session, while leaving other sessions untouched. Previously this was only unit/Cypher tested.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ClearSessionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly ShortTermMemoryService _service;

    public ClearSessionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _service = new ShortTermMemoryService(
            new Neo4jConversationRepository(fixture.TransactionRunner, NullLogger<Neo4jConversationRepository>.Instance),
            new Neo4jMessageRepository(fixture.TransactionRunner, NullLogger<Neo4jMessageRepository>.Instance),
            new Neo4jReasoningTraceRepository(fixture.TransactionRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance),
            Substitute.For<IEmbeddingOrchestrator>(),
            Substitute.For<IClock>(),
            Substitute.For<IIdGenerator>(),
            Options.Create(new ShortTermMemoryOptions()),
            NullLogger<ShortTermMemoryService>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ClearSessionAsync_RemovesAllSessionScopedNodes_AndLeavesOtherSessionsIntact()
    {
        var target = $"session-{Guid.NewGuid():N}";
        var survivor = $"session-{Guid.NewGuid():N}";

        var targetStepId = await SeedSessionAsync(target);
        var survivorStepId = await SeedSessionAsync(survivor);

        await _service.ClearSessionAsync(target);

        // Target session: every node type is gone.
        (await CountAsync("MATCH (m:Message {session_id: $sid}) RETURN count(m) AS c", target)).Should().Be(0);
        (await CountAsync("MATCH (c:Conversation {session_id: $sid}) RETURN count(c) AS c", target)).Should().Be(0);
        (await CountAsync("MATCH (t:ReasoningTrace {session_id: $sid}) RETURN count(t) AS c", target)).Should().Be(0);
        (await CountByIdAsync("MATCH (s:ReasoningStep {id: $id}) RETURN count(s) AS c", targetStepId)).Should().Be(0);

        // Survivor session: untouched.
        (await CountAsync("MATCH (m:Message {session_id: $sid}) RETURN count(m) AS c", survivor)).Should().Be(1);
        (await CountAsync("MATCH (c:Conversation {session_id: $sid}) RETURN count(c) AS c", survivor)).Should().Be(1);
        (await CountAsync("MATCH (t:ReasoningTrace {session_id: $sid}) RETURN count(t) AS c", survivor)).Should().Be(1);
        (await CountByIdAsync("MATCH (s:ReasoningStep {id: $id}) RETURN count(s) AS c", survivorStepId)).Should().Be(1);
    }

    [Fact]
    public async Task ClearSessionAsync_OnUnknownSession_IsNoOp()
    {
        var existing = $"session-{Guid.NewGuid():N}";
        await SeedSessionAsync(existing);

        await _service.ClearSessionAsync($"session-does-not-exist-{Guid.NewGuid():N}");

        (await CountAsync("MATCH (c:Conversation {session_id: $sid}) RETURN count(c) AS c", existing)).Should().Be(1);
        (await CountAsync("MATCH (m:Message {session_id: $sid}) RETURN count(m) AS c", existing)).Should().Be(1);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Seeds one conversation, one message, and one reasoning trace (with a HAS_STEP child)
    /// for <paramref name="sessionId"/>. Returns the seeded ReasoningStep id.</summary>
    private async Task<string> SeedSessionAsync(string sessionId)
    {
        var convRepo = new Neo4jConversationRepository(_fixture.TransactionRunner, NullLogger<Neo4jConversationRepository>.Instance);
        var msgRepo = new Neo4jMessageRepository(_fixture.TransactionRunner, NullLogger<Neo4jMessageRepository>.Instance);

        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = sessionId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await convRepo.UpsertAsync(conv);

        await msgRepo.AddAsync(new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = sessionId,
            Role = "user",
            Content = "hello",
            TimestampUtc = DateTimeOffset.UtcNow
        });

        // Seed a ReasoningTrace + HAS_STEP child directly so the delete cascade can be asserted.
        var traceId = $"trace-{Guid.NewGuid():N}";
        var stepId = $"step-{Guid.NewGuid():N}";
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            @"CREATE (t:ReasoningTrace {id: $tid, session_id: $sid, task: 'seed task'})
              CREATE (s:ReasoningStep {id: $stid, step_number: 1})
              CREATE (t)-[:HAS_STEP]->(s)",
            new { tid = traceId, sid = sessionId, stid = stepId });

        return stepId;
    }

    private Task<long> CountAsync(string cypher, string sessionId) =>
        RunCountAsync(cypher, new { sid = sessionId });

    private Task<long> CountByIdAsync(string cypher, string id) =>
        RunCountAsync(cypher, new { id });

    private async Task<long> RunCountAsync(string cypher, object parameters)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher, parameters);
        var record = await cursor.SingleAsync();
        return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
    }
}
