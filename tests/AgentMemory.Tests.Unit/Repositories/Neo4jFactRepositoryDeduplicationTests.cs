using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;
using NSubstitute.Core;

namespace AgentMemory.Tests.Unit.Repositories;

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
                // The driver exposes RunAsync(string, object) AND RunAsync(string, IDictionary<...>),
                // and which one a call binds to depends on the STATIC type of the argument. Capturing
                // only the object overload meant a repository method passing a dictionary recorded
                // nothing at all, and the test failed with "the collection is empty" rather than with
                // a wrong value - a harness gap that looks exactly like a broken query.
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<IDictionary<string, object>>(1)));
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
        // Still case-insensitive, now via MemoryTripleCanonicalizer rather than Cypher's
        // toLower - which is the point: the two disagree on U+0130, and only the canonicalizer
        // matches what the write path stored.
        calls[0].Cypher.Should().Contain("f.subject_key = $subjectKey");
        calls[0].Cypher.Should().Contain("f.predicate_key = $predicateKey");
        calls[0].Cypher.Should().Contain("f.object_key = $objectKey");
        calls[0].Cypher.Should().Contain("LIMIT 1");
    }

    [Fact]
    public async Task FindByTripleAsync_PassesSubjectParameter()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.FindByTripleAsync("Alice", "works_at", "Neo4j");
        var param = calls[0].Parameters!;
        // Now a dictionary rather than an anonymous object: the scoped and unscoped
        // branches share one parameter path. The value is CANONICALIZED, which is the
        // behaviour change - the lookup key must match what the write path stored.
        ((IDictionary<string, object>)param)["subjectKey"].Should().Be("alice");
    }

    [Fact]
    public async Task FindByTripleAsync_PassesPredicateParameter()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.FindByTripleAsync("Alice", "works_at", "Neo4j");
        var param = calls[0].Parameters!;
        // Now a dictionary rather than an anonymous object: the scoped and unscoped
        // branches share one parameter path. The value is CANONICALIZED, which is the
        // behaviour change - the lookup key must match what the write path stored.
        ((IDictionary<string, object>)param)["predicateKey"].Should().Be("works at");
        // "works_at" canonicalizes to "works at" -- MemoryTripleCanonicalizer.Canonical maps
        // underscores to spaces so predicates match the relation vocabulary's surface style. The
        // lookup MUST use the canonical form, because that is what the write path stored.
    }

    [Fact]
    public async Task FindByTripleAsync_PassesObjectParameter()
    {
        var (repo, calls) = CreateReadCapture();
        await repo.FindByTripleAsync("Alice", "works_at", "Neo4j");
        var param = calls[0].Parameters!;
        // Now a dictionary rather than an anonymous object: the scoped and unscoped
        // branches share one parameter path. The value is CANONICALIZED, which is the
        // behaviour change - the lookup key must match what the write path stored.
        ((IDictionary<string, object>)param)["objectKey"].Should().Be("neo4j");
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

        // Asserted on the VALUES rather than on the Cypher text. Case-insensitivity is a property of
        // the lookup, not of any particular clause, and it now comes from the same canonicalizer the
        // write path uses instead of from Cypher's toLower -- the two disagree on U+0130, which is
        // why matching the stored key is what actually makes the lookup case-insensitive.
        var param = (IDictionary<string, object>)calls[0].Parameters!;
        param["subjectKey"].Should().Be("alice");
        param["predicateKey"].Should().Be("works at");
        param["objectKey"].Should().Be("neo4j");
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
        // The triple is the dedup key, scoped per owner (owner_key keeps shared vs owned facts distinct, R1).
        calls[0].Cypher.Should().Contain("MERGE (f:Fact {subject_key: $subjectKey, predicate_key: $predicateKey, object_key: $objectKey, owner_key: $ownerKey})");
        // The canonical key is what deduplicates; the raw triple must still be persisted.
        calls[0].Cypher.Should().Contain("f.subject            = $subject");
        calls[0].Cypher.Should().Contain("f.predicate          = $predicate");
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
