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

    /// <summary>
    /// The as-of hop returns only what was BELIEVED and TRUE at those instants.
    /// </summary>
    /// <remarks>
    /// An alias does not exempt a fact from time. The live overload judges belief by
    /// <c>invalidated_at IS NULL</c>, so reusing it from a point-in-time recall volunteers facts the
    /// system did not yet know — and the extra rows are indistinguishable from ones the alias
    /// legitimately reached.
    /// </remarks>
    [Fact]
    public async Task TheAsOfHopExcludesWhatWasNotYetKnownOrNoLongerTrue()
    {
        await SeedEntityAsync("flat", aliases: ["the place on Ferrow Row"]);
        await SeedFactAsync("seed", aboutEntity: "flat", createdAt: D(2025, 1, 1));
        await SeedFactAsync("believed-and-true", aboutEntity: "flat", createdAt: D(2025, 1, 1));
        // Recorded AFTER the transaction instant: the system did not know it yet.
        await SeedFactAsync("not-yet-known", aboutEntity: "flat", createdAt: D(2025, 8, 1));
        // Retracted before the transaction instant: no longer believed.
        await SeedFactAsync("retracted-before", aboutEntity: "flat", createdAt: D(2025, 1, 1),
            invalidatedAt: D(2025, 3, 1));
        // Validity opens after the as-of instant: not true then.
        await SeedFactAsync("not-yet-true", aboutEntity: "flat", createdAt: D(2025, 1, 1),
            validFrom: D(2025, 9, 1));
        // Validity closed before it: no longer true then.
        await SeedFactAsync("no-longer-true", aboutEntity: "flat", createdAt: D(2025, 1, 1),
            validUntil: D(2025, 3, 1));

        var instant = D(2025, 6, 1);

        var reached = (await _facts.GetFactsSharingAliasedEntitiesAsOfAsync(
            ["seed"], validAsOf: instant, systemAsOf: instant, limit: 50,
            scope: MemoryScope.For(Owner))).Select(f => f.FactId).ToArray();

        reached.Should().Contain("believed-and-true");
        reached.Should().NotContain("not-yet-known", "created_at is after the transaction instant");
        reached.Should().NotContain("retracted-before", "invalidated_at precedes the transaction instant");
        reached.Should().NotContain("not-yet-true", "valid_from is after the as-of instant");
        reached.Should().NotContain("no-longer-true", "valid_until precedes the as-of instant");
    }

    /// <summary>
    /// THE CONTROL: the live hop still sees what the as-of one filters.
    /// </summary>
    /// <remarks>
    /// Without this, a query matching nothing at all would pass the test above for entirely the wrong
    /// reason — excluding four facts by being broken rather than by being correct. The same trap D2's
    /// integration test exists to avoid, where it caught a real one.
    /// </remarks>
    [Fact]
    public async Task TheLiveHopStillSeesWhatTheAsOfOneFilters()
    {
        await SeedEntityAsync("flat", aliases: ["the place on Ferrow Row"]);
        await SeedFactAsync("seed", aboutEntity: "flat", createdAt: D(2025, 1, 1));
        await SeedFactAsync("not-yet-known", aboutEntity: "flat", createdAt: D(2025, 8, 1));

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            ["seed"], limit: 50, scope: MemoryScope.For(Owner));

        reached.Select(f => f.FactId).Should().Contain("not-yet-known",
            "the live hop has no transaction clock, so the as-of exclusion above is the CLOCK doing "
            + "work rather than the query matching nothing");
    }

    /// <summary>
    /// A seed from another owner is not a way into this owner's graph.
    /// </summary>
    /// <remarks>
    /// The scope predicate was applied only to the returned fact in the first cut, leaving the seed
    /// and the bridge entity unrestricted. The repository takes seed ids as an argument, so "the
    /// caller only ever passes its own facts" is a property of today's call site, not of the
    /// contract — and a join is exactly where a boundary has to be re-asserted rather than assumed.
    /// </remarks>
    [Fact]
    public async Task AForeignSeedCannotTraverseIntoAnotherOwnersGraph()
    {
        await SeedEntityAsync("theirs", aliases: ["their other name"], owner: "owner-other");
        await SeedFactAsync("their-seed", aboutEntity: "theirs", owner: "owner-other");
        await SeedFactAsync("their-fact", aboutEntity: "theirs", owner: "owner-other");

        var reached = await _facts.GetFactsSharingAliasedEntitiesAsync(
            ["their-seed"], limit: 50, scope: MemoryScope.For(Owner));

        reached.Should().BeEmpty(
            "every node on the path is scoped, not only the fact that is returned");
    }

    private static DateTimeOffset D(int y, int m, int d) => new(y, m, d, 0, 0, 0, TimeSpan.Zero);

    private async Task SeedEntityAsync(string id, string[] aliases, string owner = Owner)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            @"MERGE (e:Entity {id: $id})
              SET e.name = $id, e.owner_id = $owner, e.aliases = $aliases",
            new { id, owner, aliases });
    }

    /// <summary>
    /// Written as raw Cypher rather than through the repository, so the test pins the SHAPE the
    /// traversal depends on — the <c>:ABOUT</c> edge and the <c>aliases</c> property — instead of
    /// whatever the write path happens to produce today.
    /// </summary>
    private async Task SeedFactAsync(
        string id, string aboutEntity, bool invalidated = false, string owner = Owner,
        DateTimeOffset? createdAt = null, DateTimeOffset? invalidatedAt = null,
        DateTimeOffset? validFrom = null, DateTimeOffset? validUntil = null)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            @"MERGE (f:Fact {id: $id})
              SET f.subject = $id, f.predicate = 'took_delivery_at', f.object = 'somewhere',
                  f.confidence = 0.9, f.owner_id = $owner,
                  f.created_at  = datetime($createdAt),
                  f.valid_from  = CASE WHEN $validFrom IS NULL THEN null ELSE datetime($validFrom) END,
                  f.valid_until = CASE WHEN $validUntil IS NULL THEN null ELSE datetime($validUntil) END,
                  f.invalidated_at = CASE
                      WHEN $invalidatedAt IS NOT NULL THEN datetime($invalidatedAt)
                      WHEN $invalidated THEN datetime()
                      ELSE null END
              WITH f
              MATCH (e:Entity {id: $aboutEntity})
              MERGE (f)-[:ABOUT]->(e)",
            new
            {
                id, owner, invalidated, aboutEntity,
                createdAt = (createdAt ?? DateTimeOffset.UtcNow).UtcDateTime.ToString("O"),
                invalidatedAt = invalidatedAt?.UtcDateTime.ToString("O"),
                validFrom = validFrom?.UtcDateTime.ToString("O"),
                validUntil = validUntil?.UtcDateTime.ToString("O"),
            });
    }
}
