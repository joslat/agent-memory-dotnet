using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Neo4j.Driver;
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

    /// <summary>F8 (40.60): a fact's ABOUT link follows its entity into the survivor; another owner's does not.</summary>
    [Fact]
    public async Task A_merge_moves_the_owners_fact_links_to_the_survivor_and_leaves_other_owners_alone()
    {
        var facts = new Neo4jFactRepository(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        var tombstone = await Person("Ana", owner: null!);
        var survivor = await Person("Ana López", owner: null!);
        async Task<Fact> FactOf(string owner, string @object)
        {
            var fact = await facts.UpsertAsync(new Fact
            {
                FactId = Guid.NewGuid().ToString("N"), Subject = "Ana", Predicate = "lives in", Object = @object,
                Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = owner,
            });
            await facts.CreateAboutRelationshipAsync(fact.FactId, tombstone.EntityId);
            return fact;
        }
        var alices = await FactOf("alice", "Lyon");
        var bobs = await FactOf("bob", "Porto");

        (await _entities.MergeEntitiesAsync(tombstone.EntityId, survivor.EntityId, MemoryScope.For("alice", includeShared: true)))
            .Should().BeTrue();

        await using var session = _fixture.Driver.AsyncSession();
        async Task<string?> AboutOf(string factId)
        {
            var cursor = await session.RunAsync("MATCH (:Fact {id: $id})-[:ABOUT]->(e:Entity) RETURN e.id AS id", new { id = factId });
            var rows = await cursor.ToListAsync();
            return rows.Count == 1 ? ValueExtensions.As<string>(rows[0]["id"]) : null;
        }
        (await AboutOf(alices.FactId)).Should().Be(survivor.EntityId, "the owner's fact follows the merge");
        (await AboutOf(bobs.FactId)).Should().Be(tombstone.EntityId, "a scoped merge never moves another owner's link");
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
