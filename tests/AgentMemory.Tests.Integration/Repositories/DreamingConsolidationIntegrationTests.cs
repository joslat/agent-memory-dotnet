using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Repositories;

#pragma warning disable AMDREAM001

/// <summary>
/// The dreaming operations of consolidation (AMDREAM001) on a real store: generic entities closed with their connections,
/// spared when no fact names them; preferences checked against the message they came from, with the earlier message of
/// the same role in the conversation; a dry run proposes, an approved apply closes exactly what was approved.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class DreamingConsolidationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly IPreferenceSourceCheck _check = Substitute.For<IPreferenceSourceCheck>();
    private readonly Neo4jConsolidationService _svc;

    private static readonly DateTimeOffset Now = new(2027, 9, 20, 0, 0, 0, TimeSpan.Zero);

    public DreamingConsolidationIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        var idGen = Substitute.For<IIdGenerator>();
        idGen.GenerateId().Returns(_ => $"run-{Guid.NewGuid():N}");
        _svc = new Neo4jConsolidationService(fixture.TransactionRunner, clock, idGen, NullLogger<Neo4jConsolidationService>.Instance, _check);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static ConsolidationOptions Dreaming(bool dryRun, bool entities = false, bool preferences = false, IReadOnlyCollection<string>? approved = null) => new()
    {
        DryRun = dryRun,
        ArchiveExpiredConversations = false,
        RemoveDuplicatePreferences = false,
        DetectDuplicateEntities = false,
        DetectLongTraces = false,
        CloseGenericEntities = entities,
        CloseUnsaidPreferences = preferences,
        OwnerId = "alice",
        ApprovedProposals = approved,
    };

    private async Task SeedAsync()
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(@"
            CREATE (c:Conversation {id: 'c1', session_id: 's1'})
            CREATE (m0:Message {id: 'm0', role: 'user', content: $m0, created_at: datetime('2027-09-14T19:00:00Z')})
            CREATE (ma:Message {id: 'ma', role: 'assistant', content: 'That sounds lovely!', created_at: datetime('2027-09-14T19:00:05Z')})
            CREATE (m1:Message {id: 'm1', role: 'user', content: $m1, created_at: datetime('2027-09-15T10:00:00Z')})
            CREATE (c)-[:HAS_MESSAGE]->(m0), (c)-[:HAS_MESSAGE]->(ma), (c)-[:HAS_MESSAGE]->(m1)
            CREATE (e1:Entity {id: 'e1', owner_id: 'alice', name: 'School backpack', type: 'OBJECT'})
            CREATE (e2:Entity {id: 'e2', owner_id: 'alice', name: 'New school backpack', type: 'OBJECT'})
            CREATE (e3:Entity {id: 'e3', owner_id: 'alice', name: 'Vasco', type: 'PERSON'})
            CREATE (e4:Entity {id: 'e4', owner_id: 'bob', name: 'School backpack', type: 'OBJECT'})
            CREATE (e3)-[:RELATED_TO {id: 'r1', owner_id: 'alice', relation_type: 'OWNS'}]->(e1)
            CREATE (e3)-[:RELATED_TO {id: 'r2', owner_id: 'alice', relation_type: 'TRIED_ON'}]->(e2)
            CREATE (:Fact {id: 'f1', owner_id: 'alice', subject: 'Vasco', predicate: 'carries', object: 'his school backpack'})
            CREATE (p1:Preference {id: 'p1', owner_id: 'alice', category: 'food', preference: 'loves octopus'})
            CREATE (p2:Preference {id: 'p2', owner_id: 'alice', category: 'values', preference: 'values family traditions'})
            CREATE (p1)-[:EXTRACTED_FROM]->(m1), (p2)-[:EXTRACTED_FROM]->(m1)",
            new { m0 = "We had dinner at Helena's on Sunday.", m1 = "Helena's octopus got me eating octopus!" });
    }

    private async Task<string?> ClosedReasonAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher);
        var record = await cursor.SingleAsync();
        return global::Neo4j.Driver.ValueExtensions.As<string?>(record["reason"]);
    }

    [Fact]
    public async Task Generic_entities_a_fact_names_are_proposed_then_closed_with_their_connections()
    {
        await SeedAsync();

        var dry = await _svc.ConsolidateAsync(Dreaming(dryRun: true, entities: true));

        dry.Proposals.Select(p => (p.Id, p.Kind)).Should().BeEquivalentTo([("e1", "entity"), ("r1", "connection")]);
        dry.Proposals.Single(p => p.Id == "r1").Text.Should().Be("Vasco -[OWNS]-> School backpack");
        (await ClosedReasonAsync("MATCH (e:Entity {id: 'e1'}) RETURN e.invalidated_reason AS reason")).Should().BeNull("a dry run closes nothing");

        var applied = await _svc.ConsolidateAsync(Dreaming(dryRun: false, entities: true));

        applied.GenericEntitiesClosed.Should().Be(1);
        applied.GenericConnectionsClosed.Should().Be(1);
        (await ClosedReasonAsync("MATCH (e:Entity {id: 'e1'}) RETURN e.invalidated_reason AS reason")).Should().Be("consolidation");
        (await ClosedReasonAsync("MATCH ()-[r:RELATED_TO {id: 'r1'}]->() RETURN r.invalidated_reason AS reason")).Should().Be("consolidation");
        (await ClosedReasonAsync("MATCH (e:Entity {id: 'e2'}) RETURN e.invalidated_reason AS reason")).Should().BeNull("no fact names it: spared");
        (await ClosedReasonAsync("MATCH ()-[r:RELATED_TO {id: 'r2'}]->() RETURN r.invalidated_reason AS reason")).Should().BeNull();
        (await ClosedReasonAsync("MATCH (e:Entity {id: 'e4'}) RETURN e.invalidated_reason AS reason")).Should().BeNull("another owner");
        (await ClosedReasonAsync("MATCH (r:ConsolidationRun) RETURN toString(r.generic_entities_closed) AS reason")).Should().Be("1");

        var again = await _svc.ConsolidateAsync(Dreaming(dryRun: true, entities: true));
        again.Proposals.Should().BeEmpty("a closed entity is no longer live");
    }

    [Fact]
    public async Task Preferences_are_checked_against_their_message_and_an_approved_one_is_closed_without_asking_again()
    {
        await SeedAsync();
        PreferenceSourceRequest? asked = null;
        _check.FindUnsaidAsync(Arg.Do<PreferenceSourceRequest>(r => asked = r), Arg.Any<CancellationToken>()).Returns(["p2"]);

        var dry = await _svc.ConsolidateAsync(Dreaming(dryRun: true, preferences: true));

        dry.Proposals.Should().ContainSingle().Which.Text.Should().Be("values family traditions (values)");
        asked!.Message.Should().Be("Helena's octopus got me eating octopus!");
        asked.Earlier.Should().Equal("We had dinner at Helena's on Sunday."); // the assistant's reply is not the person's
        asked.SaidAt.Should().Be(new DateTimeOffset(2027, 9, 15, 10, 0, 0, TimeSpan.Zero));
        asked.Preferences.Select(p => p.Key).Should().BeEquivalentTo(["p1", "p2"]);

        _check.ClearReceivedCalls();
        var applied = await _svc.ConsolidateAsync(Dreaming(dryRun: false, preferences: true, approved: ["p2"]));

        applied.UnsaidPreferencesClosed.Should().Be(1);
        await _check.DidNotReceive().FindUnsaidAsync(Arg.Any<PreferenceSourceRequest>(), Arg.Any<CancellationToken>());
        (await ClosedReasonAsync("MATCH (p:Preference {id: 'p2'}) RETURN p.invalidated_reason AS reason")).Should().Be("consolidation");
        (await ClosedReasonAsync("MATCH (p:Preference {id: 'p1'}) RETURN p.invalidated_reason AS reason")).Should().BeNull();
    }
}
