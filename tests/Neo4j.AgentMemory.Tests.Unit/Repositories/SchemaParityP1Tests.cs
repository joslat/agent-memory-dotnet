using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for P1 schema parity fixes in entity, tool call, and related repositories.
/// </summary>
public sealed class SchemaParityP1Tests
{
    // ── Helpers ──

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateEntityWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                return work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateEntityUpsertCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Entity>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<Entity>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var cursor = Substitute.For<IResultCursor>();
                cursor.FetchAsync().Returns(Task.FromResult(false));
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(cursor);
                    });
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object?>>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateEntityBatchWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Entity>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Entity>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        var cursor = Substitute.For<IResultCursor>();
                        cursor.FetchAsync().Returns(Task.FromResult(false));
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static Entity MakeEntity(string id = "e-1", string name = "Alice", string type = "Person", string? subtype = null) =>
        new()
        {
            EntityId = id,
            Name = name,
            Type = type,
            Subtype = subtype,
            Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            SourceMessageIds = Array.Empty<string>(),
            Aliases = Array.Empty<string>(),
            Attributes = new Dictionary<string, object>(),
            Metadata = new Dictionary<string, object>()
        };

    // ── P1-2: Dynamic POLE+O Labels ──

    [Fact]
    public void BuildDynamicLabels_ValidType_ReturnsUppercaseLabel()
    {
        var labels = Neo4jEntityRepository.BuildDynamicLabels("Person", null);
        labels.Should().ContainSingle().Which.Should().Be("PERSON");
    }

    [Fact]
    public void BuildDynamicLabels_ValidTypeAndSubtype_ReturnsBothLabels()
    {
        var labels = Neo4jEntityRepository.BuildDynamicLabels("Person", "Individual");
        labels.Should().HaveCount(2);
        labels.Should().Contain("PERSON");
        labels.Should().Contain("INDIVIDUAL");
    }

    [Fact]
    public void BuildDynamicLabels_UnknownType_ReturnsEmptyList()
    {
        var labels = Neo4jEntityRepository.BuildDynamicLabels("CustomType", null);
        labels.Should().BeEmpty();
    }

    [Fact]
    public void BuildDynamicLabels_LocationType_ReturnsLabel()
    {
        var labels = Neo4jEntityRepository.BuildDynamicLabels("Location", "City");
        labels.Should().HaveCount(2);
        labels.Should().Contain("LOCATION");
        labels.Should().Contain("CITY");
    }

    [Fact]
    public void SanitizeLabel_RemovesSpecialCharacters()
    {
        Neo4jEntityRepository.SanitizeLabel("Person;DROP TABLE").Should().Be("PersonDROPTABLE");
    }

    [Fact]
    public void SanitizeLabel_AllowsUnderscores()
    {
        Neo4jEntityRepository.SanitizeLabel("MY_TYPE").Should().Be("MY_TYPE");
    }

    [Fact]
    public async Task UpsertAsync_PersonType_SendsDynamicLabelCypher()
    {
        var (repo, calls) = CreateEntityUpsertCapture();
        var entity = MakeEntity(type: "Person");

        await repo.UpsertAsync(entity);

        calls.Should().Contain(c => c.Cypher.Contains("SET e:PERSON"));
    }

    [Fact]
    public async Task UpsertAsync_UnknownType_DoesNotSendLabelCypher()
    {
        var (repo, calls) = CreateEntityUpsertCapture();
        var entity = MakeEntity(type: "CustomThing");

        await repo.UpsertAsync(entity);

        calls.Should().NotContain(c => c.Cypher.Contains("SET e:"));
    }

    [Fact]
    public async Task UpsertBatchAsync_PersonType_SendsDynamicLabelCypher()
    {
        var (repo, calls) = CreateEntityBatchWriteCapture();
        var entities = new List<Entity> { MakeEntity(type: "Organization") };

        await repo.UpsertBatchAsync(entities);

        calls.Should().Contain(c => c.Cypher.Contains("SET e:ORGANIZATION"));
    }

    // ── P1-4: MENTIONS with properties ──

    [Fact]
    public async Task AddMentionAsync_WithConfidence_PassesParameters()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.AddMentionAsync("msg-1", "ent-1", confidence: 0.95, startPos: 10, endPos: 20);

        calls.Should().ContainSingle();
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("confidence")!.GetValue(param).Should().Be(0.95);
        param.GetType().GetProperty("startPos")!.GetValue(param).Should().Be(10);
        param.GetType().GetProperty("endPos")!.GetValue(param).Should().Be(20);
    }

    [Fact]
    public async Task AddMentionAsync_WithoutOptionalParams_StillWorks()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.AddMentionAsync("msg-1", "ent-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (m)-[r:MENTIONS]->(e)");
    }

    [Fact]
    public async Task AddMentionsBatchAsync_WithConfidence_PassesParameter()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.AddMentionsBatchAsync("msg-1", ["e1", "e2"], confidence: 0.8);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("confidence")!.GetValue(param).Should().Be(0.8);
    }

    // ── P1-5: EXTRACTED_FROM with properties ──

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_WithAllParams_SendsCorrectCypher()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("e-1", "msg-1",
            confidence: 0.95, startPos: 5, endPos: 15, context: "surrounding text");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (e)-[r:EXTRACTED_FROM]->(m)");
        calls[0].Cypher.Should().Contain("r.confidence = $confidence");
        calls[0].Cypher.Should().Contain("r.start_pos = $startPos");
        calls[0].Cypher.Should().Contain("r.end_pos = $endPos");
        calls[0].Cypher.Should().Contain("r.context = $context");
        calls[0].Cypher.Should().Contain("r.created_at = datetime()");
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_WithAllParams_PassesParameters()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("e-5", "msg-10",
            confidence: 0.88, startPos: 3, endPos: 25, context: "test context");

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("entityId")!.GetValue(param).Should().Be("e-5");
        param.GetType().GetProperty("messageId")!.GetValue(param).Should().Be("msg-10");
        param.GetType().GetProperty("confidence")!.GetValue(param).Should().Be(0.88);
        param.GetType().GetProperty("startPos")!.GetValue(param).Should().Be(3);
        param.GetType().GetProperty("endPos")!.GetValue(param).Should().Be(25);
        param.GetType().GetProperty("context")!.GetValue(param).Should().Be("test context");
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_OnMatchUpdatesHigherConfidence()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("e-1", "msg-1", confidence: 0.9);

        calls[0].Cypher.Should().Contain("ON MATCH SET r.confidence = CASE WHEN $confidence");
    }

    // ── P1-6: Full Tool Aggregate Stats ──
    // These tests verify the Tool MERGE Cypher by checking the raw queries sent.
    // We test via the simpler CreateTriggeredByRelationshipAsync pattern and directly
    // verify the tool MERGE Cypher strings in the repository source.

    [Fact]
    public void ToolCallRepository_ToolMergeCypher_ContainsAggregateStats()
    {
        // Verify by reading the source code's cypher string
        // The actual Cypher is embedded in Neo4jToolCallRepository.AddAsync
        var repoType = typeof(Neo4jToolCallRepository);
        var sourceCode = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "Neo4j.AgentMemory.Neo4j",
                "Repositories", "Neo4jToolCallRepository.cs"));

        sourceCode.Should().Contain("tool.successful_calls");
        sourceCode.Should().Contain("tool.failed_calls");
        sourceCode.Should().Contain("tool.total_duration_ms");
        sourceCode.Should().Contain("tool.last_used_at = datetime()");
    }

    [Fact]
    public void ToolCallRepository_ToolMergeCypher_OnCreateInitializesCounters()
    {
        var sourceCode = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "Neo4j.AgentMemory.Neo4j",
                "Repositories", "Neo4jToolCallRepository.cs"));

        sourceCode.Should().Contain("ON CREATE SET tool.created_at = datetime()");
        sourceCode.Should().Contain("tool.total_calls = 0");
        sourceCode.Should().Contain("tool.successful_calls = 0");
        sourceCode.Should().Contain("tool.failed_calls = 0");
        sourceCode.Should().Contain("tool.total_duration_ms = 0");
    }

    [Fact]
    public void ToolCallRepository_ToolMergeCypher_IncrementsBasedOnStatus()
    {
        var sourceCode = System.IO.File.ReadAllText(
            System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "..", "..", "src", "Neo4j.AgentMemory.Neo4j",
                "Repositories", "Neo4jToolCallRepository.cs"));

        sourceCode.Should().Contain("CASE WHEN $status = 'success' THEN 1 ELSE 0 END");
        sourceCode.Should().Contain("CASE WHEN $status IN ['error', 'failure', 'timeout'] THEN 1 ELSE 0 END");
        sourceCode.Should().Contain("COALESCE($durationMs, 0)");
    }

    // ── P1-7: SAME_AS with status + ON CREATE/ON MATCH ──

    [Fact]
    public async Task AddSameAsRelationshipAsync_UsesOnCreateOnMatch()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.AddSameAsRelationshipAsync("e1", "e2", 0.9, "exact");

        calls[0].Cypher.Should().Contain("ON CREATE SET r.confidence = $confidence");
        calls[0].Cypher.Should().Contain("r.status = $status");
        calls[0].Cypher.Should().Contain("r.created_at = datetime()");
        calls[0].Cypher.Should().Contain("ON MATCH SET r.confidence = CASE WHEN $confidence > r.confidence");
        calls[0].Cypher.Should().Contain("r.updated_at = datetime()");
    }

    [Fact]
    public async Task AddSameAsRelationshipAsync_DefaultStatusIsPending()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.AddSameAsRelationshipAsync("e1", "e2", 0.9, "exact");

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("status")!.GetValue(param).Should().Be("pending");
    }

    [Fact]
    public async Task AddSameAsRelationshipAsync_CustomStatus()
    {
        var (repo, calls) = CreateEntityWriteCapture();

        await repo.AddSameAsRelationshipAsync("e1", "e2", 0.9, "exact", status: "confirmed");

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("status")!.GetValue(param).Should().Be("confirmed");
    }

    // ── P1-8: Entity updated_at on ON MATCH ──

    [Fact]
    public async Task UpsertAsync_OnMatch_SetsUpdatedAt()
    {
        var (repo, calls) = CreateEntityUpsertCapture();
        var entity = MakeEntity();

        await repo.UpsertAsync(entity);

        var mergeCypher = calls[0].Cypher;
        mergeCypher.Should().Contain("ON MATCH SET");
        mergeCypher.Should().Contain("e.updated_at         = datetime()");
    }

    [Fact]
    public async Task UpsertBatchAsync_OnMatch_SetsUpdatedAt()
    {
        var (repo, calls) = CreateEntityBatchWriteCapture();
        var entities = new List<Entity> { MakeEntity() };

        await repo.UpsertBatchAsync(entities);

        var mergeCypher = calls[0].Cypher;
        mergeCypher.Should().Contain("e.updated_at         = datetime()");
    }
}
