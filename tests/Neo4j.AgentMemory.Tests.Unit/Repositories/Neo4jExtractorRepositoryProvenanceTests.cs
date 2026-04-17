using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Queries;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for provenance-related methods on <see cref="Neo4jExtractorRepository"/> (G4, G6, G7, G8).
/// </summary>
public sealed class Neo4jExtractorRepositoryProvenanceTests
{
    // ── G4: GetProvenanceAsync ──

    private static (Neo4jExtractorRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateReadCapture_EntityProvenance(params IRecord[] records)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<EntityProvenance?>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<EntityProvenance?>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(records));
                    });
                return await work(runner);
            });
        return (new Neo4jExtractorRepository(txRunner, NullLogger<Neo4jExtractorRepository>.Instance), calls);
    }

    [Fact]
    public async Task GetProvenanceAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture_EntityProvenance();
        await repo.GetProvenanceAsync("e-1");
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(ExtractorQueries.GetEntityProvenance);
    }

    [Fact]
    public async Task GetProvenanceAsync_PassesEntityIdParameter()
    {
        var (repo, calls) = CreateReadCapture_EntityProvenance();
        await repo.GetProvenanceAsync("e-42");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("entityId")!.GetValue(param).Should().Be("e-42");
    }

    [Fact]
    public async Task GetProvenanceAsync_ReturnsNullWhenNoRecords()
    {
        var (repo, _) = CreateReadCapture_EntityProvenance();
        var result = await repo.GetProvenanceAsync("e-missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetProvenanceAsync_CypherContainsExtractedFromAndBy()
    {
        var (repo, calls) = CreateReadCapture_EntityProvenance();
        await repo.GetProvenanceAsync("e-1");
        calls[0].Cypher.Should().Contain("EXTRACTED_FROM");
        calls[0].Cypher.Should().Contain("EXTRACTED_BY");
        calls[0].Cypher.Should().Contain("MATCH (e:Entity {id: $entityId})");
    }

    // ── G6: GetExtractionStatsAsync ──

    private static (Neo4jExtractorRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateReadCapture_ExtractionStats(params IRecord[] records)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ExtractionStats>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<ExtractionStats>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(records));
                    });
                return await work(runner);
            });
        return (new Neo4jExtractorRepository(txRunner, NullLogger<Neo4jExtractorRepository>.Instance), calls);
    }

    [Fact]
    public async Task GetExtractionStatsAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture_ExtractionStats();
        await repo.GetExtractionStatsAsync();
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(ExtractorQueries.GetExtractionStats);
    }

    [Fact]
    public async Task GetExtractionStatsAsync_ReturnsZerosWhenNoRecords()
    {
        var (repo, _) = CreateReadCapture_ExtractionStats();
        var result = await repo.GetExtractionStatsAsync();
        result.TotalEntities.Should().Be(0);
        result.TotalMessages.Should().Be(0);
        result.AvgEntitiesPerMessage.Should().Be(0.0);
    }

    [Fact]
    public async Task GetExtractionStatsAsync_CypherAggregatesCorrectly()
    {
        var (repo, calls) = CreateReadCapture_ExtractionStats();
        await repo.GetExtractionStatsAsync();
        calls[0].Cypher.Should().Contain("COUNT(e) AS totalEntities");
        calls[0].Cypher.Should().Contain("COUNT(DISTINCT m) AS totalMessages");
        calls[0].Cypher.Should().Contain("avgPerMessage");
    }

    // ── G7: GetExtractorStatsAsync ──

    private static (Neo4jExtractorRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateReadCapture_ExtractorStats(params IRecord[] records)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<ExtractorStats?>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<ExtractorStats?>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(records));
                    });
                return await work(runner);
            });
        return (new Neo4jExtractorRepository(txRunner, NullLogger<Neo4jExtractorRepository>.Instance), calls);
    }

    [Fact]
    public async Task GetExtractorStatsAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture_ExtractorStats();
        await repo.GetExtractorStatsAsync("my-extractor");
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(ExtractorQueries.GetExtractorStats);
    }

    [Fact]
    public async Task GetExtractorStatsAsync_PassesExtractorNameParameter()
    {
        var (repo, calls) = CreateReadCapture_ExtractorStats();
        await repo.GetExtractorStatsAsync("azure-language");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("extractorName")!.GetValue(param).Should().Be("azure-language");
    }

    [Fact]
    public async Task GetExtractorStatsAsync_ReturnsNullWhenNoRecords()
    {
        var (repo, _) = CreateReadCapture_ExtractorStats();
        var result = await repo.GetExtractorStatsAsync("nonexistent");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetExtractorStatsAsync_CypherMatchesExtractor()
    {
        var (repo, calls) = CreateReadCapture_ExtractorStats();
        await repo.GetExtractorStatsAsync("test");
        calls[0].Cypher.Should().Contain("MATCH (ex:Extractor {name: $extractorName})");
        calls[0].Cypher.Should().Contain("EXTRACTED_BY");
        calls[0].Cypher.Should().Contain("AVG(eb.confidence)");
    }

    // ── G8: DeleteProvenanceAsync ──

    private static (Neo4jExtractorRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateWriteCapture_DeleteProvenance(int deletedCount)
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<int>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var record = Substitute.For<IRecord>();
                record["deleted"].Returns((object)deletedCount);
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor(record));
                    });
                return await work(runner);
            });
        return (new Neo4jExtractorRepository(txRunner, NullLogger<Neo4jExtractorRepository>.Instance), calls);
    }

    [Fact]
    public async Task DeleteProvenanceAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture_DeleteProvenance(3);
        await repo.DeleteProvenanceAsync("e-1");
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Be(ExtractorQueries.DeleteEntityProvenance);
    }

    [Fact]
    public async Task DeleteProvenanceAsync_PassesEntityIdParameter()
    {
        var (repo, calls) = CreateWriteCapture_DeleteProvenance(0);
        await repo.DeleteProvenanceAsync("e-42");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("entityId")!.GetValue(param).Should().Be("e-42");
    }

    [Fact]
    public async Task DeleteProvenanceAsync_ReturnsDeletedCount()
    {
        var (repo, _) = CreateWriteCapture_DeleteProvenance(5);
        var result = await repo.DeleteProvenanceAsync("e-1");
        result.Should().Be(5);
    }

    [Fact]
    public async Task DeleteProvenanceAsync_ReturnsZeroWhenNothing()
    {
        var (repo, _) = CreateWriteCapture_DeleteProvenance(0);
        var result = await repo.DeleteProvenanceAsync("e-missing");
        result.Should().Be(0);
    }

    [Fact]
    public async Task DeleteProvenanceAsync_CypherDeletesBothRelationshipTypes()
    {
        var (repo, calls) = CreateWriteCapture_DeleteProvenance(2);
        await repo.DeleteProvenanceAsync("e-1");
        calls[0].Cypher.Should().Contain("EXTRACTED_FROM");
        calls[0].Cypher.Should().Contain("EXTRACTED_BY");
        calls[0].Cypher.Should().Contain("DELETE ef, eb");
    }

    [Fact]
    public async Task DeleteProvenanceAsync_UsesWriteTransaction()
    {
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));
        var repo = new Neo4jExtractorRepository(txRunner, NullLogger<Neo4jExtractorRepository>.Instance);

        await repo.DeleteProvenanceAsync("e-1");

        await txRunner.Received(1).WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<int>>>(), Arg.Any<CancellationToken>());
    }
}
