using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// I-7 review round 4: the user's person entity is found live only. After "Ana" is merged into "Ana López",
/// both match the name "Ana" (the tombstone by name, the survivor by alias); a new edge must never start
/// at the tombstone, which recall and the profile block exclude.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class EntityFindLiveByNameIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;
    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    public EntityFindLiveByNameIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Entity> Person(string name, string owner = "alice", params string[] aliases) => _entities.UpsertAsync(new Entity
    {
        EntityId = Guid.NewGuid().ToString("N"), Name = name, Type = "PERSON", Confidence = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = owner, Aliases = aliases,
    });

    [Fact]
    public async Task A_name_merged_into_another_person_finds_the_survivor()
    {
        var tombstone = await Person("Ana");
        var survivor = await Person("Ana López");
        (await _entities.MergeEntitiesAsync(tombstone.EntityId, survivor.EntityId, Alice)).Should().BeTrue();

        var found = await _entities.FindLiveByNameAsync("Ana", "PERSON", Alice);

        found!.EntityId.Should().Be(survivor.EntityId);
    }

    [Fact]
    public async Task An_invalidated_person_is_not_found()
    {
        var ana = await Person("Ana");
        await _entities.InvalidateAsync(ana.EntityId, Alice);

        (await _entities.FindLiveByNameAsync("Ana", "PERSON", Alice)).Should().BeNull();
    }

    [Fact]
    public async Task An_exact_name_wins_over_an_alias_and_case_does_not_matter()
    {
        await Person("Dana Reyes", aliases: "Dana");
        var dana = await Person("Dana");

        (await _entities.FindLiveByNameAsync("dana", "PERSON", Alice))!.EntityId.Should().Be(dana.EntityId);
    }

    [Fact]
    public async Task Another_owners_person_is_never_found()
    {
        await Person("Ana", owner: "bob");

        (await _entities.FindLiveByNameAsync("Ana", "PERSON", Alice)).Should().BeNull();
    }
}
