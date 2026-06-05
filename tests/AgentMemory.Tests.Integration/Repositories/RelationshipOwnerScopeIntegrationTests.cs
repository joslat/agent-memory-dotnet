using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Owner-isolation for RELATED_TO relationships (R1 / IC2): owner_id is persisted on the edge and a
/// scoped relationship read returns only the owner's (plus shared) edges.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class RelationshipOwnerScopeIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jRelationshipRepository _repo;

    public RelationshipOwnerScopeIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _repo = new Neo4jRelationshipRepository(fixture.TransactionRunner, NullLogger<Neo4jRelationshipRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Relationship Rel(string source, string target, string? ownerId) => new()
    {
        RelationshipId = $"r-{Guid.NewGuid():N}",
        SourceEntityId = source,
        TargetEntityId = target,
        RelationshipType = "KNOWS",
        OwnerId = ownerId,
        Confidence = 0.9,
        SourceMessageIds = Array.Empty<string>(),
        CreatedAtUtc = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task ScopedGetByEntity_ReturnsOwnerAndShared_NotOtherUsers()
    {
        await _repo.UpsertAsync(Rel("A", "B", ownerId: "alice"));
        await _repo.UpsertAsync(Rel("A", "B", ownerId: "bob"));
        await _repo.UpsertAsync(Rel("A", "B", ownerId: null)); // shared

        var results = await _repo.GetByEntityAsync("A", MemoryScope.For("alice"));

        results.Select(r => r.OwnerId).Should().Contain(new[] { "alice", (string?)null });
        results.Select(r => r.OwnerId).Should().NotContain("bob");
    }

    [Fact]
    public async Task Unscoped_ReturnsEveryone()
    {
        await _repo.UpsertAsync(Rel("A", "B", ownerId: "alice"));
        await _repo.UpsertAsync(Rel("A", "B", ownerId: "bob"));

        var results = await _repo.GetByEntityAsync("A");

        results.Select(r => r.OwnerId).Should().Contain(new[] { "alice", "bob" });
    }

    [Fact]
    public async Task OwnerId_RoundTrips_OnWriteAndRead()
    {
        var rel = Rel("A", "B", ownerId: "alice");
        await _repo.UpsertAsync(rel);

        var read = await _repo.GetByIdAsync(rel.RelationshipId);

        read.Should().NotBeNull();
        read!.OwnerId.Should().Be("alice");
    }
}
