using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ReasoningTraceRepositoryIntegrationTests
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jReasoningTraceRepository _repo;

    private static readonly float[] TestEmbedding = [0.5f, 0.5f, 0.0f, 0.0f];
    private static readonly float[] QueryEmbedding = [0.5f, 0.5f, 0.0f, 0.0f];

    public ReasoningTraceRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jReasoningTraceRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jReasoningTraceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task AddAsync_CreatesTrace_WithRequiredProperties()
    {
        var trace = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            Task = "Analyze user sentiment from recent messages",
            StartedAtUtc = new DateTimeOffset(2025, 5, 1, 9, 0, 0, TimeSpan.Zero)
        };

        var result = await _repo.AddAsync(trace);

        result.TraceId.Should().Be(trace.TraceId);
        result.SessionId.Should().Be(trace.SessionId);
        result.Task.Should().Be("Analyze user sentiment from recent messages");
        result.StartedAtUtc.Should().BeCloseTo(trace.StartedAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsTrace_WhenExists()
    {
        var trace = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            Task = "Round-trip test task",
            Outcome = "Completed successfully",
            Success = true,
            StartedAtUtc = new DateTimeOffset(2025, 4, 10, 14, 0, 0, TimeSpan.Zero),
            CompletedAtUtc = new DateTimeOffset(2025, 4, 10, 14, 5, 0, TimeSpan.Zero)
        };
        await _repo.AddAsync(trace);

        var result = await _repo.GetByIdAsync(trace.TraceId);

        result.Should().NotBeNull();
        result!.TraceId.Should().Be(trace.TraceId);
        result.Task.Should().Be("Round-trip test task");
        result.Outcome.Should().Be("Completed successfully");
        result.Success.Should().BeTrue();
        result.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task GetByIdAsync_ReturnsNull_WhenNotFound()
    {
        var result = await _repo.GetByIdAsync("trace-does-not-exist");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ListBySessionAsync_ReturnsTracesForSession_OrderedNewestFirst()
    {
        var sessionId = $"session-{Guid.NewGuid():N}";
        var trace1 = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = sessionId,
            Task = "First task",
            StartedAtUtc = new DateTimeOffset(2025, 1, 1, 8, 0, 0, TimeSpan.Zero)
        };
        var trace2 = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = sessionId,
            Task = "Second task",
            StartedAtUtc = new DateTimeOffset(2025, 1, 2, 8, 0, 0, TimeSpan.Zero)
        };
        var other = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = $"session-other-{Guid.NewGuid():N}",
            Task = "Other session task",
            StartedAtUtc = new DateTimeOffset(2025, 1, 3, 8, 0, 0, TimeSpan.Zero)
        };

        await _repo.AddAsync(trace1);
        await _repo.AddAsync(trace2);
        await _repo.AddAsync(other);

        var results = await _repo.ListBySessionAsync(sessionId);

        results.Should().HaveCount(2);
        results[0].TraceId.Should().Be(trace2.TraceId); // newest first
        results[1].TraceId.Should().Be(trace1.TraceId);
    }

    [Fact]
    public async Task UpdateAsync_PersistsOutcomeAndSuccess()
    {
        var traceId = $"trace-{Guid.NewGuid():N}";
        var trace = new ReasoningTrace
        {
            TraceId = traceId,
            SessionId = $"session-{Guid.NewGuid():N}",
            Task = "Updateable task",
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.AddAsync(trace);

        var updated = trace with
        {
            Outcome = "Task completed",
            Success = true,
            CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(3)
        };
        await _repo.UpdateAsync(updated);

        var fetched = await _repo.GetByIdAsync(traceId);
        fetched.Should().NotBeNull();
        fetched!.Outcome.Should().Be("Task completed");
        fetched.Success.Should().BeTrue();
        fetched.CompletedAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task SearchByTaskVectorAsync_ReturnsTraces_WhenEmbeddingMatches()
    {
        var trace = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            Task = "Vector searchable task",
            TaskEmbedding = TestEmbedding,
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.AddAsync(trace);

        var results = await _repo.SearchByTaskVectorAsync(QueryEmbedding, limit: 5);

        results.Should().NotBeEmpty();
        results[0].Trace.TraceId.Should().Be(trace.TraceId);
        results[0].Score.Should().BeGreaterThan(0.0);
    }

    [Fact]
    public async Task CreateConversationTraceRelationshipsAsync_CreatesHasTraceAndInSession()
    {
        var convRepo = new Neo4jConversationRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jConversationRepository>.Instance);

        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await convRepo.UpsertAsync(conv);

        var trace = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = conv.SessionId,
            Task = "Linked trace task",
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.AddAsync(trace);

        await _repo.CreateConversationTraceRelationshipsAsync(conv.ConversationId, trace.TraceId);

        var hasTrace = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (c:Conversation {id: $cid})-[:HAS_TRACE]->(t:ReasoningTrace {id: $tid}) RETURN count(*) AS c",
                new { cid = conv.ConversationId, tid = trace.TraceId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        var inSession = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (t:ReasoningTrace {id: $tid})-[:IN_SESSION]->(c:Conversation {id: $cid}) RETURN count(*) AS c",
                new { tid = trace.TraceId, cid = conv.ConversationId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        hasTrace.Should().Be(1);
        inSession.Should().Be(1);
    }

    [Fact]
    public async Task CreateInitiatedByRelationshipAsync_LinksTraceToMessage()
    {
        var convRepo = new Neo4jConversationRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jConversationRepository>.Instance);
        var msgRepo = new Neo4jMessageRepository(
            _fixture.TransactionRunner,
            NullLogger<Neo4jMessageRepository>.Instance);

        var conv = new Conversation
        {
            ConversationId = $"conv-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        await convRepo.UpsertAsync(conv);

        var msg = new Message
        {
            MessageId = $"msg-{Guid.NewGuid():N}",
            ConversationId = conv.ConversationId,
            SessionId = conv.SessionId,
            Role = "user",
            Content = "Please analyze this",
            TimestampUtc = DateTimeOffset.UtcNow
        };
        await msgRepo.AddAsync(msg);

        var trace = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = conv.SessionId,
            Task = "Analysis task",
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        await _repo.AddAsync(trace);

        await _repo.CreateInitiatedByRelationshipAsync(trace.TraceId, msg.MessageId);

        var count = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (t:ReasoningTrace {id: $tid})-[:INITIATED_BY]->(m:Message {id: $mid}) RETURN count(*) AS c",
                new { tid = trace.TraceId, mid = msg.MessageId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

        count.Should().Be(1);
    }
}
