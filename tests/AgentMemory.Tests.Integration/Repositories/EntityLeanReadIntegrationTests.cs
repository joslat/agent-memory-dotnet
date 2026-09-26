using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The vector-free candidate read (D15) returns exactly what the full read returns, minus the vector.
/// It projects its properties by name, so this pins it to the mapper it feeds.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class EntityLeanReadIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _repo;

    public EntityLeanReadIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_lean_read_equals_the_full_read_without_the_vector()
    {
        var full = new Entity
        {
            EntityId = $"entity-{Guid.NewGuid():N}", OwnerId = "owner-lean", Name = "Carla Mendes", CanonicalName = "carla mendes",
            Type = "PERSON", Subtype = "friend", Description = "plays chess", Confidence = 0.9, Aliases = ["Carla"],
            Attributes = new Dictionary<string, object> { ["city"] = "Braga" }, SourceMessageIds = ["m1", "m2"],
            Latitude = 41.55, Longitude = -8.42, CreatedAtUtc = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
            Embedding = [0.4f, 0.3f, 0.2f, 0.1f],
        };
        var bare = full with
        {
            EntityId = $"entity-{Guid.NewGuid():N}", Name = "Tomas", CanonicalName = null, Aliases = [], SourceMessageIds = [],
            Subtype = null, Description = null, Latitude = null, Longitude = null, Attributes = new Dictionary<string, object>(),
        };
        await _repo.UpsertAsync(full);
        await _repo.UpsertAsync(bare);
        var scope = MemoryScope.For("owner-lean");

        var withVectors = await _repo.GetByTypeAsync("PERSON", scope);
        var lean = await _repo.GetByTypeWithoutEmbeddingAsync("PERSON", scope);

        withVectors.Should().HaveCount(2);
        lean.Should().OnlyContain(e => e.Embedding == null);
        // Attributes/metadata hold JSON values, which structural comparison cannot equate: compared as JSON.
        lean.Should().BeEquivalentTo(withVectors.Select(e => e with { Embedding = null }), o => o
            .WithoutStrictOrdering().Excluding(e => e.Attributes).Excluding(e => e.Metadata));
        foreach (var entity in lean)
        {
            var twin = withVectors.Single(w => w.EntityId == entity.EntityId);
            System.Text.Json.JsonSerializer.Serialize(entity.Attributes).Should().Be(System.Text.Json.JsonSerializer.Serialize(twin.Attributes));
            System.Text.Json.JsonSerializer.Serialize(entity.Metadata).Should().Be(System.Text.Json.JsonSerializer.Serialize(twin.Metadata));
        }
    }
}
