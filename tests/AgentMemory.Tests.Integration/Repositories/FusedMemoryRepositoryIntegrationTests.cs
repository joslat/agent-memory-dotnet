using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class FusedMemoryRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jPreferenceRepository _preferences;

    public FusedMemoryRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _entities = new Neo4jEntityRepository(
            fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _preferences = new Neo4jPreferenceRepository(
            fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            "UNWIND $ids AS id CREATE (:Message {id: id})",
            new { ids = new[] { "message-1", "message-2" } });
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EntityFusedBatch_PersistsEmbeddingLocationDynamicLabelsAndProvenance()
    {
        var persisted = await _entities.UpsertFusedBatchAsync(
        [
            new Entity
            {
                EntityId = "entity-1",
                Name = "Zurich office",
                Type = "Location",
                Subtype = "Office",
                Confidence = 0.9,
                Embedding = [0.1f, 0.2f, 0.3f, 0.4f],
                Latitude = 47.3769,
                Longitude = 8.5417,
                OwnerId = "owner-1",
                SourceMessageIds = ["message-1", "message-2"],
                CreatedAtUtc = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            },
        ]);

        persisted.Should().ContainSingle();
        var read = await _entities.GetByIdAsync("entity-1");
        read.Should().NotBeNull();
        read!.Embedding.Should().Equal(0.1f, 0.2f, 0.3f, 0.4f);
        read.Latitude.Should().BeApproximately(47.3769, 1e-6);
        read.Longitude.Should().BeApproximately(8.5417, 1e-6);

        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(@"
            MATCH (e:Entity {id: 'entity-1'})
            OPTIONAL MATCH (e)-[r:EXTRACTED_FROM]->(:Message)
            RETURN labels(e) AS labels, count(r) AS provenance");
        var record = await cursor.SingleAsync();
        var expectedLabels = new[] { "Entity" }
            .Concat(Neo4jEntityRepository.BuildDynamicLabels("Location", "Office"));
        global::Neo4j.Driver.ValueExtensions.As<IReadOnlyList<string>>(record["labels"])
            .Should().Contain(expectedLabels);
        global::Neo4j.Driver.ValueExtensions.As<long>(record["provenance"])
            .Should().Be(2);
    }

    [Fact]
    public async Task FactAndPreferenceFusedSingletons_PreserveNaturalKeyEmbeddingAndProvenance()
    {
        var first = Fact("fact-1", [0.1f, 0.2f, 0.3f, 0.4f]);
        var second = Fact("fact-2", [0.4f, 0.3f, 0.2f, 0.1f]);

        (await _facts.UpsertFusedBatchAsync([first])).Single().FactId.Should().Be("fact-1");
        var merged = (await _facts.UpsertFusedBatchAsync([second])).Single();
        merged.FactId.Should().Be("fact-1", "the natural triple keeps its original stable identifier");
        (await _facts.GetByIdAsync("fact-1"))!.Embedding.Should().Equal(second.Embedding!);

        await _preferences.UpsertFusedBatchAsync(
        [
            new Preference
            {
                PreferenceId = "preference-1",
                Category = "drink",
                PreferenceText = "coffee",
                Confidence = 0.9,
                Embedding = [0.2f, 0.4f, 0.6f, 0.8f],
                OwnerId = "owner-1",
                SourceMessageIds = ["message-1", "message-2"],
                CreatedAtUtc = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
            },
        ]);
        (await _preferences.GetByIdAsync("preference-1"))!.Embedding.Should()
            .Equal(0.2f, 0.4f, 0.6f, 0.8f);

        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(@"
            MATCH (n)-[r:EXTRACTED_FROM]->(:Message)
            WHERE n.id IN ['fact-1', 'preference-1']
            RETURN n.id AS id, count(r) AS provenance ORDER BY id");
        var records = await cursor.ToListAsync();
        records.Should().HaveCount(2);
        records.Should().OnlyContain(record =>
            global::Neo4j.Driver.ValueExtensions.As<long>(record["provenance"]) == 2);
    }

    private static Fact Fact(string id, float[] embedding) => new()
    {
        FactId = id,
        Subject = "Alice",
        Predicate = "likes",
        Object = "coffee",
        Confidence = 0.9,
        Embedding = embedding,
        OwnerId = "owner-1",
        SourceMessageIds = ["message-1", "message-2"],
        CreatedAtUtc = DateTimeOffset.Parse("2026-08-04T12:00:00Z"),
    };
}
