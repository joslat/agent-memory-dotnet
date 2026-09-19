using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// E-1 against live Neo4j: the hop crosses a DECLARED identity, and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests assert at the assembler seam over substitutes, so they prove the hop is taken and
/// its results merged. They cannot prove the Cypher selects the right facts, or that it parses.
/// </para>
/// <para>
/// <b>The control here is the point of the whole test.</b> A traversal that returned facts through
/// ANY shared entity would pass a naive version of the first case while being a different feature —
/// the one already measured, as a node-distance re-ranker over the same edges, and found to HARM the
/// vertical by promoting structurally-near facts over relevant ones. What makes this an identity join
/// rather than an adjacency join is that the entity carries an alias, so an un-aliased entity must
/// return nothing however well connected it is.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class IdentityExpansionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private const string Owner = "owner-e1";

    public IdentityExpansionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The whole feature: the fact filed under the other name comes back.</summary>
    [Fact]
    public async Task AFactFiledUnderTheOtherNameIsReachedThroughTheAlias()
    {
        // "The new flat is the place on Ferrow Row" -- one entity, two names.
        await SeedEntityAsync("flat", aliases: ["the place on Ferrow Row"]);
        await SeedFactAsync("seed-alias-declaration", aboutEntity: "flat");
        await SeedFactAsync("delivery-under-the-other-name", aboutEntity: "flat");

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            ["seed-alias-declaration"], limit: 50, scope: MemoryScope.For(Owner));

        reached.Select(f => f.FactId).Should().Contain("delivery-under-the-other-name");
    }

    /// <summary>
    /// THE CONTROL: an entity with no alias is not a bridge, however connected it is.
    /// </summary>
    /// <remarks>
    /// Without this, a traversal that ignored <c>aliases</c> entirely would still pass the test above
    /// — and would be the adjacency hop that already measured as harmful, shipped under the name of
    /// the one that should help.
    /// </remarks>
    [Fact]
    public async Task AnUnaliasedEntityIsNotABridge()
    {
        await SeedEntityAsync("plain", aliases: []);
        await SeedFactAsync("seed-plain", aboutEntity: "plain");
        await SeedFactAsync("sibling-through-plain", aboutEntity: "plain");

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            ["seed-plain"], limit: 50, scope: MemoryScope.For(Owner));

        reached.Select(f => f.FactId).Should().NotContain("sibling-through-plain",
            "sharing a subject is not the same as being the same thing under another name");
    }

    /// <summary>The seeds are not re-delivered: the caller already has them.</summary>
    [Fact]
    public async Task TheSeedsThemselvesAreExcluded()
    {
        await SeedEntityAsync("flat", aliases: ["the place on Ferrow Row"]);
        await SeedFactAsync("seed-a", aboutEntity: "flat");
        await SeedFactAsync("seed-b", aboutEntity: "flat");

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            ["seed-a", "seed-b"], limit: 50, scope: MemoryScope.For(Owner));

        reached.Select(f => f.FactId).Should().NotContain("seed-a").And.NotContain("seed-b");
    }

    /// <summary>A retracted fact is not volunteered by a join any more than by a search.</summary>
    [Fact]
    public async Task AnInvalidatedFactIsNotReached()
    {
        await SeedEntityAsync("flat", aliases: ["the place on Ferrow Row"]);
        await SeedFactAsync("seed", aboutEntity: "flat");
        await SeedFactAsync("retracted", aboutEntity: "flat", invalidated: true);

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            ["seed"], limit: 50, scope: MemoryScope.For(Owner));

        reached.Select(f => f.FactId).Should().NotContain("retracted");
    }

    /// <summary>
    /// Owner scoping holds, and on this path it matters more than most.
    /// </summary>
    /// <remarks>
    /// An alias is a join. A join that crossed the owner boundary would let one tenant's facts answer
    /// another tenant's question, and it would look like a feature working rather than a leak.
    /// </remarks>
    [Fact]
    public async Task TheHopIsScopedToTheOwner()
    {
        await SeedEntityAsync("flat", aliases: ["the place on Ferrow Row"]);
        await SeedFactAsync("seed", aboutEntity: "flat");
        await SeedFactAsync("theirs", aboutEntity: "flat", owner: "owner-other");

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            ["seed"], limit: 50, scope: MemoryScope.For(Owner));

        reached.Select(f => f.FactId).Should().NotContain("theirs");
    }

    /// <summary>The cap is a cap.</summary>
    [Fact]
    public async Task TheLimitBoundsWhatOnePopularReferentCanSpend()
    {
        await SeedEntityAsync("flat", aliases: ["the place on Ferrow Row"]);
        await SeedFactAsync("seed", aboutEntity: "flat");
        for (var i = 0; i < 6; i++)
            await SeedFactAsync($"other-{i}", aboutEntity: "flat");

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            ["seed"], limit: 2, scope: MemoryScope.For(Owner));

        reached.Should().HaveCount(2);
    }

    /// <summary>No seeds is no traversal, not an unanchored one.</summary>
    [Fact]
    public async Task NoSeedsReturnsNothingRatherThanEverything()
    {
        await SeedEntityAsync("flat", aliases: ["the place on Ferrow Row"]);
        await SeedFactAsync("orphan", aboutEntity: "flat");

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            [], limit: 50, scope: MemoryScope.For(Owner));

        reached.Should().BeEmpty();
    }

    private async Task SeedEntityAsync(string id, string[] aliases)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            @"MERGE (e:Entity {id: $id})
              SET e.name = $id, e.owner_id = $owner, e.aliases = $aliases",
            new { id, owner = Owner, aliases });
    }

    /// <summary>
    /// Written as raw Cypher rather than through the repository, so the test pins the SHAPE the
    /// traversal depends on — the <c>:ABOUT</c> edge and the <c>aliases</c> property — instead of
    /// whatever the write path happens to produce today.
    /// </summary>
    private async Task SeedFactAsync(
        string id, string aboutEntity, bool invalidated = false, string owner = Owner)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            @"MERGE (f:Fact {id: $id})
              SET f.subject = $id, f.predicate = 'took_delivery_at', f.object = 'somewhere',
                  f.confidence = 0.9, f.owner_id = $owner,
                  f.created_at = datetime(),
                  f.invalidated_at = CASE WHEN $invalidated THEN datetime() ELSE null END
              WITH f
              MATCH (e:Entity {id: $aboutEntity})
              MERGE (f)-[:ABOUT]->(e)",
            new { id, owner, invalidated, aboutEntity });
    }
}
