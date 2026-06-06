using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Integration tests for the <c>(:ReasoningStep)-[:TOUCHED]->(:Entity)</c> audit edges
/// (upstream parity, neo4j-labs/agent-memory PR #113).
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ReasoningStepTouchedIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jReasoningTraceRepository _traceRepo;
    private readonly Neo4jReasoningStepRepository _stepRepo;
    private readonly Neo4jEntityRepository _entityRepo;

    public ReasoningStepTouchedIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _traceRepo = new Neo4jReasoningTraceRepository(
            fixture.TransactionRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance);
        _stepRepo = new Neo4jReasoningStepRepository(
            fixture.TransactionRunner, NullLogger<Neo4jReasoningStepRepository>.Instance);
        _entityRepo = new Neo4jEntityRepository(
            fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<string> CreateStepAsync()
    {
        var trace = await _traceRepo.AddAsync(new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            Task = "Touched-edge test",
            StartedAtUtc = DateTimeOffset.UtcNow
        });

        var step = await _stepRepo.AddAsync(new ReasoningStep
        {
            StepId = $"step-{Guid.NewGuid():N}",
            TraceId = trace.TraceId,
            StepNumber = 1,
            Thought = "Looking things up"
        });

        return step.StepId;
    }

    private async Task<string> CreateEntityAsync()
    {
        var entity = await _entityRepo.UpsertAsync(new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}",
            Name = $"Entity {Guid.NewGuid():N}",
            Type = "Concept",
            Confidence = 1.0,
            CreatedAtUtc = DateTimeOffset.UtcNow
        });
        return entity.EntityId;
    }

    private Task<long> CountTouchedEdgesAsync(string stepId) =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (s:ReasoningStep {id: $stepId})-[:TOUCHED]->(:Entity) RETURN count(*) AS c",
                new { stepId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]);
        });

    [Fact]
    public async Task LinkTouchedEntitiesAsync_CreatesEdges_AndIsReadableBack()
    {
        var stepId = await CreateStepAsync();
        var e1 = await CreateEntityAsync();
        var e2 = await CreateEntityAsync();

        var linked = await _stepRepo.LinkTouchedEntitiesAsync(stepId, new[] { e1, e2 });

        linked.Should().Be(2);
        (await CountTouchedEdgesAsync(stepId)).Should().Be(2);

        var touched = await _stepRepo.GetTouchedEntityIdsAsync(stepId);
        touched.Should().BeEquivalentTo(new[] { e1, e2 });
    }

    [Fact]
    public async Task LinkTouchedEntitiesAsync_IsIdempotent_NoDuplicateEdges()
    {
        var stepId = await CreateStepAsync();
        var e1 = await CreateEntityAsync();

        await _stepRepo.LinkTouchedEntitiesAsync(stepId, new[] { e1 });
        await _stepRepo.LinkTouchedEntitiesAsync(stepId, new[] { e1 });

        (await CountTouchedEdgesAsync(stepId)).Should().Be(1);
    }

    [Fact]
    public async Task LinkTouchedEntitiesAsync_SkipsNonExistentEntities()
    {
        var stepId = await CreateStepAsync();
        var e1 = await CreateEntityAsync();

        var linked = await _stepRepo.LinkTouchedEntitiesAsync(
            stepId, new[] { e1, "entity-does-not-exist" });

        linked.Should().Be(1);
        (await CountTouchedEdgesAsync(stepId)).Should().Be(1);
    }

    [Fact]
    public async Task LinkTouchedEntitiesAsync_StampsRecordedAtOnEdge()
    {
        var stepId = await CreateStepAsync();
        var e1 = await CreateEntityAsync();

        await _stepRepo.LinkTouchedEntitiesAsync(stepId, new[] { e1 });

        var hasRecordedAt = await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (s:ReasoningStep {id: $stepId})-[r:TOUCHED]->(:Entity) " +
                "RETURN r.recorded_at IS NOT NULL AS hasTs",
                new { stepId });
            var record = await cursor.SingleAsync();
            return global::Neo4j.Driver.ValueExtensions.As<bool>(record["hasTs"]);
        });

        hasRecordedAt.Should().BeTrue();
    }

    [Fact]
    public async Task GetTouchedEntityIdsAsync_ReturnsEmpty_WhenNoEdges()
    {
        var stepId = await CreateStepAsync();

        var touched = await _stepRepo.GetTouchedEntityIdsAsync(stepId);

        touched.Should().BeEmpty();
    }
}
