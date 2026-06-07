using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Owner-isolation for the entity dedup vector read <see cref="Neo4jEntityRepository.FindSimilarByEmbeddingAsync"/>
/// (R1, scope-optional hook). Identical embeddings → every candidate scores 1.0, so only the owner WHERE
/// filter (plus over-fetch) keeps a foreign owner's entities out of a scoped "find duplicates" result.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class EntityVectorReadScopeIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;

    private static readonly float[] Embedding = [0.3f, 0.1f, 0.4f, 0.2f];

    public EntityVectorReadScopeIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Entity NewEntity(string name, string? ownerId) => new()
    {
        EntityId = $"e-{Guid.NewGuid():N}",
        Name = name,
        Type = "Organization",
        Confidence = 0.9,
        OwnerId = ownerId,
        Embedding = Embedding,
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task FindSimilarByEmbedding_Scoped_ExcludesOtherOwners_KeepsShared()
    {
        var source = NewEntity("Acme HQ", ownerId: "alice");
        await _entities.UpsertAsync(source);
        await _entities.UpsertAsync(NewEntity("Acme Subsidiary", ownerId: "alice"));
        await _entities.UpsertAsync(NewEntity("Acme Rival", ownerId: "bob"));
        await _entities.UpsertAsync(NewEntity("Acme Standard", ownerId: null)); // shared

        var results = await _entities.FindSimilarByEmbeddingAsync(
            source.EntityId, minSimilarity: 0.0, limit: 10, scope: MemoryScope.For("alice"));

        var owners = results.Select(r => r.Entity.OwnerId).ToList();
        owners.Should().Contain("alice");
        owners.Should().Contain((string?)null);   // shared visible
        owners.Should().NotContain("bob");          // other owner's entity never leaks
    }

    [Fact]
    public async Task FindSimilarByEmbedding_Unscoped_ReturnsEveryone()
    {
        var source = NewEntity("Acme HQ", ownerId: "alice");
        await _entities.UpsertAsync(source);
        await _entities.UpsertAsync(NewEntity("Acme Rival", ownerId: "bob"));
        await _entities.UpsertAsync(NewEntity("Acme Standard", ownerId: null));

        var results = await _entities.FindSimilarByEmbeddingAsync(source.EntityId, minSimilarity: 0.0, limit: 10);

        results.Select(r => r.Entity.OwnerId).Should().Contain(new[] { "bob", (string?)null });
    }
}
