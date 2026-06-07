using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Owner-isolation for the entity-provenance audit read (leak-4). The provenance lookup is by entity id
/// but returns more than a plain entity (source message ids + extractor metadata), so an owner-scoped
/// call must not return provenance for another owner's private entity.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ExtractorProvenanceScopeIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jExtractorRepository _extractors;
    private readonly Neo4jEntityRepository _entities;

    public ExtractorProvenanceScopeIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _extractors = new Neo4jExtractorRepository(fixture.TransactionRunner, NullLogger<Neo4jExtractorRepository>.Instance);
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task GetProvenanceAsync_ForeignOwnerScope_ReturnsNull()
    {
        var id = $"e-{Guid.NewGuid():N}";
        await _entities.UpsertAsync(new Entity { EntityId = id, Name = "Bob private", Type = "Concept", OwnerId = "bob", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        (await _extractors.GetProvenanceAsync(id, MemoryScope.For("alice"))).Should().BeNull("alice must not see bob's entity provenance");
        (await _extractors.GetProvenanceAsync(id, MemoryScope.For("bob")))!.EntityId.Should().Be(id);
        (await _extractors.GetProvenanceAsync(id))!.EntityId.Should().Be(id); // unscoped (admin/audit) still works
    }

    [Fact]
    public async Task GetProvenanceAsync_Scoped_FindsSharedEntity()
    {
        var id = $"e-{Guid.NewGuid():N}";
        await _entities.UpsertAsync(new Entity { EntityId = id, Name = "Shared", Type = "Concept", OwnerId = null, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        (await _extractors.GetProvenanceAsync(id, MemoryScope.For("alice")))!.EntityId.Should().Be(id, "shared entities remain visible to a scoped owner");
    }
}
