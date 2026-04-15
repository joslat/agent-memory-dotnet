using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for <see cref="Neo4jExtractorRepository"/>.
/// Cypher strings and parameter objects are captured via NSubstitute — no real Neo4j connection.
/// </summary>
public sealed class Neo4jExtractorRepositoryTests
{
    private static (Neo4jExtractorRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateWriteCapture()
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

        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Extractor>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<Extractor>>>();
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
                return await work(runner);
            });

        return (new Neo4jExtractorRepository(txRunner, NullLogger<Neo4jExtractorRepository>.Instance), calls);
    }

    private static (Neo4jExtractorRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateReadCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();

        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Extractor?>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<Extractor?>>>();
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
                return await work(runner);
            });

        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Extractor>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Extractor>>>>();
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
                return await work(runner);
            });

        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<(Entity, double)>>>>();
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
                return await work(runner);
            });

        return (new Neo4jExtractorRepository(txRunner, NullLogger<Neo4jExtractorRepository>.Instance), calls);
    }

    // ── UpsertAsync ──

    [Fact]
    public async Task UpsertAsync_SendsCorrectMergeCypher()
    {
        var (repo, calls) = CreateWriteCapture();
        var extractor = new Extractor
        {
            ExtractorId = "ext-1",
            Name = "llm-extractor",
            Version = "1.0",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await repo.UpsertAsync(extractor);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (ex:Extractor {name: $name})");
        calls[0].Cypher.Should().Contain("ON CREATE SET ex.id = $id");
        calls[0].Cypher.Should().Contain("ex.version = $version");
        calls[0].Cypher.Should().Contain("ex.created_at = datetime()");
        calls[0].Cypher.Should().Contain("ON MATCH SET");
    }

    [Fact]
    public async Task UpsertAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();
        var extractor = new Extractor
        {
            ExtractorId = "ext-2",
            Name = "azure-language",
            Version = "2.0",
            ConfigJson = "{\"model\":\"gpt-4\"}",
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

        await repo.UpsertAsync(extractor);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("id")!.GetValue(param).Should().Be("ext-2");
        param.GetType().GetProperty("name")!.GetValue(param).Should().Be("azure-language");
        param.GetType().GetProperty("version")!.GetValue(param).Should().Be("2.0");
        param.GetType().GetProperty("config")!.GetValue(param).Should().Be("{\"model\":\"gpt-4\"}");
    }

    // ── GetByNameAsync ──

    [Fact]
    public async Task GetByNameAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.GetByNameAsync("llm-extractor");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (ex:Extractor {name: $name})");
    }

    [Fact]
    public async Task GetByNameAsync_ReturnsNullWhenNotFound()
    {
        var (repo, _) = CreateReadCapture();

        var result = await repo.GetByNameAsync("nonexistent");

        result.Should().BeNull();
    }

    // ── ListAsync ──

    [Fact]
    public async Task ListAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.ListAsync();

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (ex:Extractor)");
        calls[0].Cypher.Should().Contain("ORDER BY ex.name");
    }

    [Fact]
    public async Task ListAsync_ReturnsEmptyWhenNoExtractors()
    {
        var (repo, _) = CreateReadCapture();

        var result = await repo.ListAsync();

        result.Should().BeEmpty();
    }

    // ── CreateExtractedByRelationshipAsync ──

    [Fact]
    public async Task CreateExtractedByRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedByRelationshipAsync("e-1", "llm-extractor", 0.95);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (e:Entity {id: $entity_id})");
        calls[0].Cypher.Should().Contain("MATCH (ex:Extractor {name: $extractor_name})");
        calls[0].Cypher.Should().Contain("MERGE (e)-[r:EXTRACTED_BY]->(ex)");
        calls[0].Cypher.Should().Contain("r.confidence = $confidence");
        calls[0].Cypher.Should().Contain("r.extraction_time_ms = $extraction_time_ms");
        calls[0].Cypher.Should().Contain("r.created_at = datetime()");
    }

    [Fact]
    public async Task CreateExtractedByRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedByRelationshipAsync("entity-42", "my-extractor", 0.88, 150);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("entity_id")!.GetValue(param).Should().Be("entity-42");
        param.GetType().GetProperty("extractor_name")!.GetValue(param).Should().Be("my-extractor");
        param.GetType().GetProperty("confidence")!.GetValue(param).Should().Be(0.88);
        param.GetType().GetProperty("extraction_time_ms")!.GetValue(param).Should().Be(150);
    }

    [Fact]
    public async Task CreateExtractedByRelationshipAsync_ExtractionTimeMsIsOptional()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedByRelationshipAsync("e-1", "extractor", 0.9);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("extraction_time_ms")!.GetValue(param).Should().BeNull();
    }

    // ── GetEntitiesByExtractorAsync ──

    [Fact]
    public async Task GetEntitiesByExtractorAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture();

        await repo.GetEntitiesByExtractorAsync("llm-extractor", 50);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (ex:Extractor {name: $extractor_name})<-[r:EXTRACTED_BY]-(e:Entity)");
        calls[0].Cypher.Should().Contain("ORDER BY e.created_at DESC");
        calls[0].Cypher.Should().Contain("LIMIT $limit");
    }

    [Fact]
    public async Task GetEntitiesByExtractorAsync_ReturnsEmptyWhenNoMatches()
    {
        var (repo, _) = CreateReadCapture();

        var result = await repo.GetEntitiesByExtractorAsync("nonexistent");

        result.Should().BeEmpty();
    }
}
