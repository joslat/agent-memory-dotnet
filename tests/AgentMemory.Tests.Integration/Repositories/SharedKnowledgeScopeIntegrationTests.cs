using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// G-15, against real Cypher: shared knowledge resolves against shared entities only (a book's “Alice” never
/// meets a user's private “Alice”), and every owner's recall finds it.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class SharedKnowledgeScopeIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow.AddMinutes(-5);
    private static readonly MemoryScope SharedOnly = MemoryScope.For("\u0001shared", includeShared: true);   // = MemoryExtractionPipeline.SharedResolutionOwner (internal to Core)
    private readonly Neo4jIntegrationFixture _fixture;

    public SharedKnowledgeScopeIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Neo4jEntityRepository Entities() => new(_fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);

    private Neo4jFactRepository Facts() => new(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance,
        memoryOptions: Options.Create(new MemoryOptions()), ownerRowCounts: new OwnerRowCounts());

    private static Entity Person(string id, string? owner) => new()
    {
        EntityId = id, Name = "Alice", Type = "PERSON", Confidence = 1, CreatedAtUtc = T0, OwnerId = owner, Embedding = [1f, 0f, 0f, 0f],
    };

    [Fact]
    public async Task The_shared_only_scope_sees_the_shared_Alice_and_never_a_private_one()
    {
        await Entities().UpsertAsync(Person("alice-private", "owner-a"));
        await Entities().UpsertAsync(Person("alice-book", null));

        var candidates = await Entities().GetByTypeAsync("PERSON", SharedOnly);

        candidates.Select(e => e.EntityId).Should().Equal("alice-book");
    }

    [Fact]
    public async Task Every_owner_recalls_shared_knowledge_beside_their_own()
    {
        var facts = Facts();
        await facts.UpsertAsync(new Fact
        {
            FactId = "book-1", Subject = "Alice", Predicate = "fell down", Object = "a rabbit hole", Confidence = 1,
            CreatedAtUtc = T0, OwnerId = null, Embedding = [1f, 0f, 0f, 0f],
        });
        await facts.UpsertAsync(new Fact
        {
            FactId = "b-own", Subject = "user", Predicate = "is reading", Object = "a book", Confidence = 1,
            CreatedAtUtc = T0, OwnerId = "owner-b", Embedding = [0.9f, 0.1f, 0f, 0f],
        });
        await facts.UpsertAsync(new Fact
        {
            FactId = "a-private", Subject = "user", Predicate = "is reading", Object = "a diary", Confidence = 1,
            CreatedAtUtc = T0, OwnerId = "owner-a", Embedding = [0.95f, 0.05f, 0f, 0f],
        });

        var forB = await facts.SearchByVectorAsync([1f, 0f, 0f, 0f], 10, 0.0, MemoryScope.For("owner-b"));
        var sharedOnly = await facts.SearchByVectorAsync([1f, 0f, 0f, 0f], 10, 0.0, SharedOnly);

        forB.Select(r => r.Fact.FactId).Should().BeEquivalentTo(["book-1", "b-own"]);
        sharedOnly.Select(r => r.Fact.FactId).Should().Equal("book-1");
    }
}
