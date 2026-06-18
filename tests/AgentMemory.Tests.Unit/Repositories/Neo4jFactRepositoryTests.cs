using System.Collections;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Unit.TestHelpers;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class Neo4jFactRepositoryTests
{
    private static (Neo4jFactRepository Repo, List<(string Cypher, object? Parameters)> Calls)
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
        return (new Neo4jFactRepository(txRunner, NullLogger<Neo4jFactRepository>.Instance), calls);
    }

    private static (Neo4jFactRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateFactBatchWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Fact>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Fact>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        var cypher = ci.Arg<string>();
                        var parameters = ci.ArgAt<object>(1);
                        calls.Add((cypher, parameters));
                        // Echo the merged nodes back for the UNWIND batch query, as real Neo4j does (one
                        // RETURN row per surviving triple). The repository resolves each node's id from this
                        // result to target its embedding/EXTRACTED_FROM sub-writes; an empty cursor would make
                        // those sub-writes silently skip, hiding regressions.
                        IResultCursor cursor = cypher.Contains("UNWIND $items")
                            ? new FakeResultCursor(BuildMergedFactRecords(parameters))
                            : new FakeResultCursor();
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });
        return (new Neo4jFactRepository(txRunner, NullLogger<Neo4jFactRepository>.Instance), calls);
    }

    // Builds one IRecord per UNWIND item, simulating the MERGE's "RETURN f" of the surviving node. Same-
    // triple items are collapsed by the repository BEFORE the query runs, so each item here is already a
    // distinct triple; the node simply echoes the item's key/identity properties.
    private static IRecord[] BuildMergedFactRecords(object parameters)
    {
        var itemsObj = parameters.GetType().GetProperty("items")!.GetValue(parameters);
        return ((IEnumerable)itemsObj!).Cast<IDictionary<string, object?>>()
            .Select(BuildMergedFactRecord).ToArray();
    }

    private static IRecord BuildMergedFactRecord(IDictionary<string, object?> item)
    {
        object Val(string key) => item[key]!;
        var node = Substitute.For<INode>();
        node["id"].Returns(Val("id"));
        node["subject"].Returns(Val("subject"));
        node["predicate"].Returns(Val("predicate"));
        node["object"].Returns(Val("object"));
        node["owner_key"].Returns(Val("owner_key"));
        node["confidence"].Returns(Val("confidence"));
        node["created_at"].Returns(Val("created_at"));
        node.Properties.Returns(new Dictionary<string, object>
        {
            ["id"]        = Val("id"),
            ["subject"]   = Val("subject"),
            ["predicate"] = Val("predicate"),
            ["object"]    = Val("object"),
            ["owner_key"] = Val("owner_key"),
            ["confidence"] = Val("confidence"),
            ["created_at"] = Val("created_at"),
        });
        var record = Substitute.For<IRecord>();
        record["f"].Returns(node);
        return record;
    }

    // ── CreateAboutRelationshipAsync ──

    [Fact]
    public async Task CreateAboutRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateAboutRelationshipAsync("f-1", "ent-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (f)-[:ABOUT]->(e)");
    }

    [Fact]
    public async Task CreateAboutRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateAboutRelationshipAsync("f-10", "ent-20");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("factId")!.GetValue(parameters).Should().Be("f-10");
        parameters.GetType().GetProperty("entityId")!.GetValue(parameters).Should().Be("ent-20");
    }

    // ── CreateExtractedFromRelationshipAsync ──

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("f-1", "msg-1");

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("MERGE (f)-[:EXTRACTED_FROM]->(m)");
    }

    [Fact]
    public async Task CreateExtractedFromRelationshipAsync_PassesCorrectParameters()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.CreateExtractedFromRelationshipAsync("f-5", "msg-7");

        var parameters = calls[0].Parameters!;
        parameters.GetType().GetProperty("factId")!.GetValue(parameters).Should().Be("f-5");
        parameters.GetType().GetProperty("messageId")!.GetValue(parameters).Should().Be("msg-7");
    }

    // ── UpsertBatchAsync ──

    [Fact]
    public async Task UpsertBatchAsync_EmptyList_ReturnsEmpty()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();

        var result = await repo.UpsertBatchAsync(Array.Empty<Fact>());

        result.Should().BeEmpty();
        calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UpsertBatchAsync_SendsUnwindCypher()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();
        var facts = new List<Fact>
        {
            new()
            {
                FactId = "f1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
                Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };

        await repo.UpsertBatchAsync(facts);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("UNWIND $items AS item");
    }

    [Fact]
    public async Task UpsertBatchAsync_SendsExtractedFromCypherForFactsWithSourceMessages()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();
        var facts = new List<Fact>
        {
            new()
            {
                FactId = "f1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
                Confidence = 0.9, SourceMessageIds = new[] { "msg-1" },
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };

        await repo.UpsertBatchAsync(facts);

        calls.Should().HaveCount(2);
        calls[1].Cypher.Should().Contain("EXTRACTED_FROM");
    }

    [Fact]
    public async Task UpsertBatchAsync_SkipsExtractedFromForFactsWithoutSourceMessages()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();
        var facts = new List<Fact>
        {
            new()
            {
                FactId = "f1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
                Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };

        await repo.UpsertBatchAsync(facts);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("UNWIND");
    }

    [Fact]
    public async Task UpsertBatchAsync_WritesCategory_InCypherAndItems()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();
        var facts = new List<Fact>
        {
            new()
            {
                FactId = "f1", Subject = "Alice", Predicate = "works_at", Object = "Neo4j",
                Category = "professional", Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            }
        };

        await repo.UpsertBatchAsync(facts);

        // The UNWIND query must set the category property...
        calls[0].Cypher.Should().Contain("f.category");

        // ...and the per-item parameter map must carry the value.
        var parameters = calls[0].Parameters!;
        var items = (IEnumerable<object>)parameters.GetType().GetProperty("items")!.GetValue(parameters)!;
        var first = (IDictionary<string, object?>)items.Cast<object>().First();
        first.Should().ContainKey("category");
        first["category"].Should().Be("professional");
    }

    // ── R5 #10: batch MERGE key aligned with the single-upsert triple ──

    private static Fact MakeFact(string id, string subject, string predicate, string @object,
        string? ownerId = null) => new()
        {
            FactId = id, Subject = subject, Predicate = predicate, Object = @object,
            OwnerId = ownerId, Confidence = 0.9, SourceMessageIds = Array.Empty<string>(),
            CreatedAtUtc = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task UpsertBatchAsync_MergesOnTriple_NotOnId()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();

        await repo.UpsertBatchAsync(new[] { MakeFact("f1", "Alice", "works_at", "Neo4j") });

        // Same idempotency key as the single Upsert path; must NOT merge on the (re-extraction-volatile) id.
        calls[0].Cypher.Should().Contain(
            "MERGE (f:Fact {subject: item.subject, predicate: item.predicate, object: item.object, owner_key: item.owner_key})");
        calls[0].Cypher.Should().NotContain("MERGE (f:Fact {id:");
    }

    [Fact]
    public async Task UpsertBatchAsync_SetsIdOnCreateOnly_NeverOnMatch()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();

        await repo.UpsertBatchAsync(new[] { MakeFact("f1", "Alice", "works_at", "Neo4j") });

        var cypher = calls[0].Cypher;
        cypher.Should().MatchRegex(@"ON CREATE SET[\s\S]*f\.id\s+=\s+item\.id");
        // f.id must not be rewritten on a match — a matched triple keeps its original primary key.
        var onMatch = cypher[cypher.IndexOf("ON MATCH SET", StringComparison.Ordinal)..];
        onMatch.Should().NotContain("f.id");
    }

    [Fact]
    public async Task UpsertBatchAsync_CoalescesValidWindow_OnMatch_AndStampsUpdatedAt()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();

        await repo.UpsertBatchAsync(new[] { MakeFact("f1", "Alice", "works_at", "Neo4j") });

        var onMatch = calls[0].Cypher[calls[0].Cypher.IndexOf("ON MATCH SET", StringComparison.Ordinal)..];
        // Preserve existing valid-time window when the incoming value is null (parity with single path),
        // rather than the old behaviour that overwrote it to null.
        onMatch.Should().Contain("ELSE f.valid_from");
        onMatch.Should().Contain("ELSE f.valid_until");
        onMatch.Should().Contain("f.updated_at         = datetime(item.updated_at)");
        // And the per-item map must carry updated_at so datetime(item.updated_at) is non-null at runtime.
        var parameters = calls[0].Parameters!;
        var items = (IEnumerable<object>)parameters.GetType().GetProperty("items")!.GetValue(parameters)!;
        ((IDictionary<string, object?>)items.Cast<object>().First()).Should().ContainKey("updated_at");
    }

    [Fact]
    public async Task UpsertBatchAsync_CollapsesSameTripleInputs_ToSingleItem_AndReturnsOne()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();

        // Two inputs, same triple+owner, different ids — must collapse to one UNWIND item and one result.
        var result = await repo.UpsertBatchAsync(new[]
        {
            MakeFact("f-first",  "Alice", "works_at", "Neo4j"),
            MakeFact("f-second", "Alice", "works_at", "Neo4j"),
        });

        var parameters = calls[0].Parameters!;
        var items = ((IEnumerable)parameters.GetType().GetProperty("items")!.GetValue(parameters)!).Cast<object>().ToList();
        items.Should().ContainSingle("same-triple inputs must be de-duplicated before the UNWIND");
        // Last-writer-wins: the surviving item keeps the last occurrence's id.
        ((IDictionary<string, object?>)items[0])["id"].Should().Be("f-second");
        result.Should().ContainSingle();
    }

    [Fact]
    public async Task UpsertBatchAsync_SameTripleDifferentOwners_ProducesDistinctItems()
    {
        var (repo, calls) = CreateFactBatchWriteCapture();

        var result = await repo.UpsertBatchAsync(new[]
        {
            MakeFact("f-alice", "Alice", "works_at", "Neo4j", ownerId: "alice"),
            MakeFact("f-bob",   "Alice", "works_at", "Neo4j", ownerId: "bob"),
            MakeFact("f-shared", "Alice", "works_at", "Neo4j", ownerId: null),
        });

        var parameters = calls[0].Parameters!;
        var items = ((IEnumerable)parameters.GetType().GetProperty("items")!.GetValue(parameters)!).Cast<object>().ToList();
        // owner_key segregates the same triple per owner — none collapse.
        items.Should().HaveCount(3);
        result.Should().HaveCount(3);
    }
}
