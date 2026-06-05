using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Owner-isolation for the NON-vector direct lookups (R1 / IC3): Fact-by-subject, Entity-by-name,
/// Preference-by-category. A scoped read must return only the owner's (plus shared) rows.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class NonVectorReadScopeIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jPreferenceRepository _prefs;

    public NonVectorReadScopeIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _prefs = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task FactGetBySubject_ScopedToOwner_ExcludesOtherUsers()
    {
        await _facts.UpsertAsync(new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "likes", Object = "tea", OwnerId = "alice", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _facts.UpsertAsync(new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "likes", Object = "coffee", OwnerId = "bob", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _facts.UpsertAsync(new Fact { FactId = $"f-{Guid.NewGuid():N}", Subject = "Alice", Predicate = "is", Object = "engineer", OwnerId = null, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        var results = await _facts.GetBySubjectAsync("Alice", MemoryScope.For("alice"));

        results.Select(f => f.OwnerId).Should().Contain(new[] { "alice", (string?)null });
        results.Select(f => f.OwnerId).Should().NotContain("bob");
    }

    [Fact]
    public async Task EntityGetByName_ScopedToOwner_ExcludesOtherUsers()
    {
        await _entities.UpsertAsync(new Entity { EntityId = $"e-{Guid.NewGuid():N}", Name = "Acme", Type = "Organization", OwnerId = "alice", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _entities.UpsertAsync(new Entity { EntityId = $"e-{Guid.NewGuid():N}", Name = "Acme", Type = "Organization", OwnerId = "bob", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _entities.UpsertAsync(new Entity { EntityId = $"e-{Guid.NewGuid():N}", Name = "Acme", Type = "Organization", OwnerId = null, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        var results = await _entities.GetByNameAsync("Acme", includeAliases: true, scope: MemoryScope.For("alice"));

        results.Select(e => e.OwnerId).Should().Contain(new[] { "alice", (string?)null });
        results.Select(e => e.OwnerId).Should().NotContain("bob");
    }

    [Fact]
    public async Task PreferenceGetByCategory_ScopedToOwner_ExcludesOtherUsers()
    {
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "dark mode", OwnerId = "alice", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "light mode", OwnerId = "bob", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "company palette", OwnerId = null, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        var results = await _prefs.GetByCategoryAsync("style", MemoryScope.For("alice"));

        results.Select(p => p.OwnerId).Should().Contain(new[] { "alice", (string?)null });
        results.Select(p => p.OwnerId).Should().NotContain("bob");
    }

    [Fact]
    public async Task Unscoped_ReturnsEveryone()
    {
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "dark mode", OwnerId = "alice", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "light mode", OwnerId = "bob", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        var results = await _prefs.GetByCategoryAsync("style"); // scope null

        results.Select(p => p.OwnerId).Should().Contain(new[] { "alice", "bob" });
    }
}
