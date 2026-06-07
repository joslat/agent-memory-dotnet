using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Owner over-fetch coverage for the Entity and Preference scoped vector reads (gap-overfetch — the
/// Fact case was already covered). Identical embeddings → every candidate ties at score 1.0, so a naive
/// LIMIT-before-WHERE would let high-volume foreign-owner rows crowd out the owner's rows; the over-fetch
/// (topK ≫ limit) plus post-WHERE keeps them.
/// <para>Note: over-fetch is a heuristic bounded by topK (Math.Max(limit×factor, limit+floor)); it
/// prevents starvation while the candidate set ≤ topK. With more tied-score foreign rows than topK it is
/// not a hard guarantee — these tests seed below that bound, matching the Fact starvation test's design.</para>
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class OverFetchStarvationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jPreferenceRepository _prefs;

    private static readonly float[] Embedding = [0.3f, 0.1f, 0.4f, 0.2f];

    public OverFetchStarvationIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _prefs = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task EntitySearchByVector_OwnerNotStarvedByForeignVolume()
    {
        for (var i = 0; i < 50; i++)
            await _entities.UpsertAsync(new Entity { EntityId = $"e-bob-{i}", Name = $"Foreign{i}", Type = "Concept", OwnerId = "bob", Confidence = 0.9, Embedding = Embedding, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _entities.UpsertAsync(new Entity { EntityId = "e-alice-1", Name = "AliceA", Type = "Concept", OwnerId = "alice", Confidence = 0.9, Embedding = Embedding, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _entities.UpsertAsync(new Entity { EntityId = "e-alice-2", Name = "AliceB", Type = "Concept", OwnerId = "alice", Confidence = 0.9, Embedding = Embedding, CreatedAtUtc = DateTimeOffset.UtcNow });

        var results = await _entities.SearchByVectorAsync(Embedding, limit: 5, scope: MemoryScope.For("alice"));

        results.Should().HaveCount(2, "both owner entities survive despite 50 higher-volume foreign rows");
        results.Select(r => r.Entity.OwnerId).Should().AllBe("alice");
    }

    [Fact]
    public async Task PreferenceSearchByVector_OwnerNotStarvedByForeignVolume()
    {
        for (var i = 0; i < 50; i++)
            await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-bob-{i}", Category = "style", PreferenceText = $"Foreign{i}", OwnerId = "bob", Confidence = 0.9, Embedding = Embedding, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _prefs.UpsertAsync(new Preference { PreferenceId = "p-alice-1", Category = "style", PreferenceText = "AliceA", OwnerId = "alice", Confidence = 0.9, Embedding = Embedding, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _prefs.UpsertAsync(new Preference { PreferenceId = "p-alice-2", Category = "style", PreferenceText = "AliceB", OwnerId = "alice", Confidence = 0.9, Embedding = Embedding, CreatedAtUtc = DateTimeOffset.UtcNow });

        var results = await _prefs.SearchByVectorAsync(Embedding, limit: 5, scope: MemoryScope.For("alice"));

        results.Should().HaveCount(2, "both owner preferences survive despite 50 higher-volume foreign rows");
        results.Select(r => r.Preference.OwnerId).Should().AllBe("alice");
    }
}
