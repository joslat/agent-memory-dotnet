using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;
using NSubstitute.Core;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jFactRepositoryDeduplicationTests
{
    // ── FindByTripleAsync ──

    private static (Neo4jFactRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateReadCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Fact?>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<Fact?>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult((IResultCursor)new FakeResultCursor());
                    });
                return await work(runner);
            });
        return (new Neo4jFactRepository(txRunner, NullLogger<Neo4jFactRepository>.Instance), calls);
    }

    [Fact]
    public async Task FindByTripleAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.FindByTripleAsync("Alice", "works_at", "Neo4j");
        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MATCH (f:Fact)");
        calls[0].Cypher.Should().Contain("toLower(f.subject) = toLower($subject)");
        calls[0].Cypher.Should().Contain("toLower(f.predicate) = toLower($predicate)");
        calls[0].Cypher.Should().Contain("toLower(f.object) = toLower($object)");
        calls[0].Cypher.Should().Contain("LIMIT 1");
    }

    [Fact]
    public async Task FindByTripleAsync_PassesSubjectParameter()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.FindByTripleAsync("Alice", "works_at", "Neo4j");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("subject")!.GetValue(param).Should().Be("Alice");
    }

    [Fact]
    public async Task FindByTripleAsync_PassesPredicateParameter()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.FindByTripleAsync("Alice", "works_at", "Neo4j");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("predicate")!.GetValue(param).Should().Be("works_at");
    }

    [Fact]
    public async Task FindByTripleAsync_PassesObjectParameter()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.FindByTripleAsync("Alice", "works_at", "Neo4j");
        var param = calls[0].Parameters!;
        param.GetType().GetProperty("object")!.GetValue(param).Should().Be("Neo4j");
    }

    [Fact]
    public async Task FindByTripleAsync_ReturnsNull_WhenNoMatch()
    {
        var (repo, _) = CreateReadCapture();
        var result = await repo.FindByTripleAsync("Unknown", "has", "nothing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task FindByTripleAsync_UsesCaseInsensitiveComparison()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.FindByTripleAsync("ALICE", "WORKS_AT", "NEO4J");
        calls[0].Cypher.Should().Contain("toLower(f.subject) = toLower($subject)");
    }

    // ── UpsertAsync uses MERGE on SPO triple ──

    private static IRecord CreateFactRecord()
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var node = Substitute.For<INode>();
        node["id"].Returns((object)"f-1");
        node["subject"].Returns((object)"Alice");
        node["predicate"].Returns((object)"works_at");
        node["object"].Returns((object)"Neo4j");
        node["confidence"].Returns((object)0.9);
        node["created_at"].Returns((object)now);
        node.Properties.Returns(new Dictionary<string, object>
        {
            ["id"] = "f-1",
            ["subject"] = "Alice",
            ["predicate"] = "works_at",
            ["object"] = "Neo4j",
            ["confidence"] = 0.9,
            ["created_at"] = now
        });
        var record = Substitute.For<IRecord>();
        record["f"].Returns(node);
        return record;
    }

    private static (Neo4jFactRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateUpsertCypherCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Fact>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<Fact>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var fakeRecord = CreateFactRecord();

                // UpsertAsync uses Dictionary<string,object?> which resolves to the IDictionary overload
                IResultCursor MakeCursor(CallInfo ci)
                {
                    calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                    return new FakeResultCursor(fakeRecord);
                }

                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>())
                    .Returns(ci => Task.FromResult(MakeCursor(ci)));
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci => Task.FromResult(MakeCursor(ci)));

                return await work(runner);
            });
        return (new Neo4jFactRepository(txRunner, NullLogger<Neo4jFactRepository>.Instance), calls);
    }

    [Fact]
    public async Task UpsertAsync_MergesOnSpoTriple()
    {
        var (repo, calls) = CreateUpsertCypherCapture();
        var fact = new Fact
        {
            FactId = "f-1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
            Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(fact);
        calls.Should().HaveCountGreaterThanOrEqualTo(1);
        calls[0].Cypher.Should().Contain("MERGE (f:Fact {subject: $subject, predicate: $predicate, object: $object})");
    }

    [Fact]
    public async Task UpsertAsync_DoesNotMergeOnId()
    {
        var (repo, calls) = CreateUpsertCypherCapture();
        var fact = new Fact
        {
            FactId = "f-1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
            Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(fact);
        calls[0].Cypher.Should().NotContain("MERGE (f:Fact {id:");
    }

    [Fact]
    public async Task UpsertAsync_SetsIdOnCreate()
    {
        var (repo, calls) = CreateUpsertCypherCapture();
        var fact = new Fact
        {
            FactId = "f-1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
            Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(fact);
        calls[0].Cypher.Should().Contain("ON CREATE SET");
        calls[0].Cypher.Should().MatchRegex(@"f\.id\s+=\s+\$id");
    }

    [Fact]
    public async Task UpsertAsync_SetsUpdatedAtOnMatch()
    {
        var (repo, calls) = CreateUpsertCypherCapture();
        var fact = new Fact
        {
            FactId = "f-1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
            Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(fact);
        calls[0].Cypher.Should().Contain("ON MATCH SET");
        calls[0].Cypher.Should().MatchRegex(@"f\.updated_at\s+=\s+datetime\(\$updatedAtUtc\)");
    }

    [Fact]
    public async Task UpsertAsync_PassesUpdatedAtUtcParameter()
    {
        var (repo, calls) = CreateUpsertCypherCapture();
        var fact = new Fact
        {
            FactId = "f-1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
            Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        await repo.UpsertAsync(fact);
        var param = calls[0].Parameters as IDictionary<string, object?>;
        param.Should().NotBeNull();
        param!.Should().ContainKey("updatedAtUtc");
    }
}
