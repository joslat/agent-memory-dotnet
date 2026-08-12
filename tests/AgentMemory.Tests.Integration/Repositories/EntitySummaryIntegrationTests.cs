using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Entity summaries in the database (S1), where the provenance edges either exist or do not.
/// </summary>
/// <remarks>
/// <para>
/// The staleness arithmetic is unit-tested. What only a live database can show is that a summary
/// actually attaches to its sources, that regenerating it does not accumulate edges to facts it no
/// longer draws on, and that one tenant's summary is invisible to another.
/// </para>
/// <para>
/// The provenance edges are the reason this is a graph node rather than a column. If they silently
/// failed to attach, every unit test would still pass and "which facts is this claim standing on?"
/// would quietly have no answer.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class EntitySummaryIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntitySummaryRepository _summaries;
    private readonly Neo4jFactRepository _facts;

    public EntitySummaryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _summaries = new Neo4jEntitySummaryRepository(
            fixture.TransactionRunner, NullLogger<Neo4jEntitySummaryRepository>.Instance);
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    private Task<Fact> FactAsync(string id, string @object, string owner = "alice") =>
        _facts.UpsertAsync(new Fact
        {
            FactId = id,
            Subject = "Alice",
            Predicate = "lives in",
            Object = @object,
            Confidence = 0.9,
            OwnerId = owner,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });

    private static EntitySummary Summary(
        string content, IReadOnlyList<string> sourceIds, string owner = "alice", string id = "sum-1") => new()
    {
        SummaryId = id,
        EntityId = "e-alice",
        Content = content,
        SourceFactIds = sourceIds,
        SourceFingerprint = $"fp-{content.GetHashCode():X}",
        OwnerId = owner,
        GeneratedAtUtc = DateTimeOffset.UtcNow,
    };

    private Task<int> ProvenanceEdgeCountAsync() =>
        _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (:EntitySummary {entity_id: 'e-alice'})-[r:EXTRACTED_FROM]->(:Fact) RETURN count(r) AS c");
            var records = await cursor.ToListAsync();
            return records.Count == 0 ? 0 : global::Neo4j.Driver.ValueExtensions.As<int>(records[0]["c"]);
        });

    [Fact]
    public async Task ASummaryRoundTrips()
    {
        await FactAsync("f-1", "Zurich");
        var stored = Summary("Alice lives in Zurich", ["f-1"]);

        await _summaries.UpsertAsync(stored);
        var read = await _summaries.GetByEntityAsync("e-alice", Alice);

        read.Should().NotBeNull();
        read!.Content.Should().Be("Alice lives in Zurich");
        read.SourceFingerprint.Should().Be(stored.SourceFingerprint);
        read.SourceFactIds.Should().Equal("f-1");
    }

    [Fact]
    public async Task ProvenanceEdgesAttachToEverySourceFact()
    {
        // The reason this is a node in a graph. If the MERGE silently matched nothing, every unit
        // test would still pass and the summary would stand on no recorded evidence at all.
        await FactAsync("f-1", "Zurich");
        await FactAsync("f-2", "Basel");

        await _summaries.UpsertAsync(Summary("two sources", ["f-1", "f-2"]));

        (await ProvenanceEdgeCountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task RegeneratingFromFewerSourcesDropsTheStaleEdges()
    {
        // THE edge case, literally. Edges are rebuilt rather than merged: a summary regenerated from a
        // smaller fact set must not keep pointing at sources its text no longer draws on, which would
        // claim provenance the content cannot support.
        await FactAsync("f-1", "Zurich");
        await FactAsync("f-2", "Basel");
        await _summaries.UpsertAsync(Summary("two sources", ["f-1", "f-2"]));

        await _summaries.UpsertAsync(Summary("one source", ["f-1"], id: "sum-2"));

        (await ProvenanceEdgeCountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task ASecondUpsertReplacesRatherThanDuplicates()
    {
        await FactAsync("f-1", "Zurich");
        await _summaries.UpsertAsync(Summary("first", ["f-1"]));
        await _summaries.UpsertAsync(Summary("second", ["f-1"], id: "sum-2"));

        var read = await _summaries.GetByEntityAsync("e-alice", Alice);

        read!.Content.Should().Be("second", "one summary per entity per owner");
    }

    [Fact]
    public async Task AnotherOwnersSummaryIsInvisible()
    {
        // R1. Two tenants summarising the same entity each did so from the facts they can see, so a
        // shared node would leak one owner's knowledge straight into another's context.
        await FactAsync("f-1", "Zurich", owner: "bob");
        await _summaries.UpsertAsync(Summary("bob's view", ["f-1"], owner: "bob"));

        (await _summaries.GetByEntityAsync("e-alice", Alice)).Should().BeNull();
    }

    [Fact]
    public async Task DeletingRemovesTheSummaryAndItsEdges()
    {
        await FactAsync("f-1", "Zurich");
        await _summaries.UpsertAsync(Summary("to be removed", ["f-1"]));

        var deleted = await _summaries.DeleteByEntityAsync("e-alice", Alice);

        deleted.Should().BeTrue();
        (await _summaries.GetByEntityAsync("e-alice", Alice)).Should().BeNull();
        (await ProvenanceEdgeCountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task ASummaryOverAMissingFactStillStores()
    {
        // A source fact removed between synthesis and write must not fail the write. The summary is
        // then short an edge, which is exactly what the fingerprint check will catch on read -- the
        // failure is detected where it can be acted on rather than thrown where it cannot.
        var act = async () => await _summaries.UpsertAsync(Summary("orphaned", ["f-missing"]));

        await act.Should().NotThrowAsync();
        (await _summaries.GetByEntityAsync("e-alice", Alice)).Should().NotBeNull();
    }
}
