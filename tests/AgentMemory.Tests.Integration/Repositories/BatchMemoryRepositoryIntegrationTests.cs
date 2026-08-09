using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class BatchMemoryRepositoryIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jPreferenceRepository _preferenceRepository;
    private readonly Neo4jRelationshipRepository _relationshipRepository;

    public BatchMemoryRepositoryIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _preferenceRepository = new Neo4jPreferenceRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jPreferenceRepository>.Instance);
        _relationshipRepository = new Neo4jRelationshipRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jRelationshipRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PreferenceBatch_RoundTripsPropertiesEmbeddingsAndProvenance()
    {
        await using (var session = _fixture.Driver.AsyncSession())
        {
            await session.RunAsync(
                "UNWIND $ids AS id CREATE (:Message {id: id})",
                new { ids = new[] { "message-1", "message-2" } });
        }

        var preferences = new[]
        {
            Preference("preference-1", "coffee", [0.1f, 0.2f, 0.3f, 0.4f]),
            Preference("preference-2", "tea", [0.4f, 0.3f, 0.2f, 0.1f])
        };

        var persisted = await _preferenceRepository.UpsertBatchAsync(preferences);

        persisted.Select(item => item.PreferenceId).Should()
            .BeEquivalentTo("preference-1", "preference-2");
        foreach (var expected in preferences)
        {
            var actual = await _preferenceRepository.GetByIdAsync(expected.PreferenceId);
            actual.Should().NotBeNull();
            actual!.OwnerId.Should().Be("owner-1");
            actual.PreferenceText.Should().Be(expected.PreferenceText);
            actual.Embedding.Should().Equal(expected.Embedding!);
            actual.Metadata.Should().ContainKey("source");
        }

        await using var verifySession = _fixture.Driver.AsyncSession();
        var cursor = await verifySession.RunAsync(
            "MATCH (:Preference)-[r:EXTRACTED_FROM]->(:Message) RETURN count(r) AS count");
        var record = await cursor.SingleAsync();
        global::Neo4j.Driver.ValueExtensions.As<long>(record["count"]).Should().Be(4);
    }

    [Fact]
    public async Task RelationshipBatch_RoundTripsOwnerTemporalAndMetadataProperties()
    {
        var validFrom = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var validUntil = DateTimeOffset.Parse("2026-12-31T00:00:00Z");
        var relationships = new[]
        {
            Relationship("relationship-1", "entity-1", "entity-2", validFrom, validUntil),
            Relationship("relationship-2", "entity-2", "entity-1", validFrom, validUntil)
        };

        var persisted = await _relationshipRepository.UpsertBatchAsync(relationships);

        persisted.Select(item => item.RelationshipId).Should()
            .BeEquivalentTo("relationship-1", "relationship-2");
        foreach (var expected in relationships)
        {
            var actual = await _relationshipRepository.GetByIdAsync(expected.RelationshipId);
            actual.Should().NotBeNull();
            actual!.OwnerId.Should().Be("owner-1");
            actual.RelationshipType.Should().Be("KNOWS");
            actual.SourceEntityId.Should().Be(expected.SourceEntityId);
            actual.TargetEntityId.Should().Be(expected.TargetEntityId);
            actual.ValidFrom.Should().Be(validFrom);
            actual.ValidUntil.Should().Be(validUntil);
            actual.Attributes.Should().ContainKey("strength");
            actual.Metadata.Should().ContainKey("source");
        }
    }

    private static Preference Preference(string id, string text, float[] embedding) => new()
    {
        PreferenceId = id,
        Category = "drink",
        PreferenceText = text,
        Context = "morning",
        Confidence = 0.9,
        Embedding = embedding,
        OwnerId = "owner-1",
        SourceMessageIds = ["message-1", "message-2"],
        CreatedAtUtc = DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
        Metadata = new Dictionary<string, object> { ["source"] = "batch-test" }
    };

    private static Relationship Relationship(
        string id,
        string sourceId,
        string targetId,
        DateTimeOffset validFrom,
        DateTimeOffset validUntil) => new()
        {
            RelationshipId = id,
            SourceEntityId = sourceId,
            TargetEntityId = targetId,
            RelationshipType = "KNOWS",
            Description = "batch relationship",
            Confidence = 0.9,
            OwnerId = "owner-1",
            SourceMessageIds = ["message-1"],
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            CreatedAtUtc = DateTimeOffset.Parse("2026-07-29T00:00:00Z"),
            Attributes = new Dictionary<string, object> { ["strength"] = "high" },
            Metadata = new Dictionary<string, object> { ["source"] = "batch-test" }
        };
}
