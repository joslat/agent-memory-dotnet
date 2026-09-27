using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>36.7 / 36.4 on a real store: live relationships with names, an ended edge left out, the first end kept.</summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class RelationshipsInRecallIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jRelationshipRepository _relationships;
    private readonly MemoryScope _owner = MemoryScope.For("owner-rel", includeShared: false);

    public RelationshipsInRecallIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _relationships = new Neo4jRelationshipRepository(fixture.TransactionRunner, NullLogger<Neo4jRelationshipRepository>.Instance);
    }

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        foreach (var name in new[] { "Oskar", "Hamburg", "Copenhagen", "Carmen", "Mallory" })
            await _entities.UpsertAsync(new Entity
            {
                EntityId = name, Name = name, Type = "PERSON", Confidence = 1, CreatedAtUtc = T0,
                OwnerId = name == "Mallory" ? "someone-else" : "owner-rel",
            });
        await Rel("home-old", "Oskar", "lives_in", "Hamburg");
        await Rel("home-new", "Oskar", "lives_in", "Copenhagen");
        await Rel("friend", "Oskar", "best_friend", "Carmen");
        await Rel("foreign", "Mallory", "knows", "Carmen", owner: "someone-else");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private Task<Relationship> Rel(string id, string source, string type, string target, string owner = "owner-rel") =>
        _relationships.UpsertAsync(new Relationship
        {
            RelationshipId = id, SourceEntityId = source, TargetEntityId = target, RelationshipType = type,
            Confidence = 0.9, CreatedAtUtc = T0, OwnerId = owner,
        });

    [Fact]
    public async Task Live_relationships_touching_the_entities_come_with_both_names_and_nothing_foreign()
    {
        var found = await _relationships.GetLiveAmongAsync(["Carmen", "Copenhagen"], 10, T0, _owner);

        found.Select(r => $"{r.SourceName} {r.Relationship.RelationshipType} {r.TargetName}")
            .Should().BeEquivalentTo(["Oskar best_friend Carmen", "Oskar lives_in Copenhagen"]);
    }

    [Fact]
    public async Task An_ended_relationship_is_no_longer_live_and_its_first_end_is_kept()
    {
        (await _relationships.EndAsync("home-old", T0, _owner)).Should().BeTrue();
        (await _relationships.EndAsync("home-old", T0.AddDays(5), _owner)).Should().BeTrue();

        var live = await _relationships.GetLiveAmongAsync(["Oskar"], 10, T0.AddDays(1), _owner);
        live.Select(r => r.Relationship.RelationshipId).Should().NotContain("home-old").And.Contain("home-new");
        (await _relationships.GetByIdAsync("home-old"))!.ValidUntil.Should().Be(T0);
    }

    [Fact]
    public async Task Another_owners_relationship_cannot_be_ended()
    {
        (await _relationships.EndAsync("foreign", T0, _owner)).Should().BeFalse();
        (await _relationships.GetByIdAsync("foreign"))!.ValidUntil.Should().BeNull();
    }
}
