using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 30.8 steps 1 and 5, against a live graph: the prune stamps <i>why</i>, and the probe reads only
/// what decayed.
/// </summary>
/// <remarks>
/// <para>
/// The partition being tested is between two states that look identical in every other query:
/// <b>decayed</b> (the system let this go) and <b>superseded</b> (the system replaced it). Only the
/// first can honestly be reported as forgotten; reporting the second would tell the user information
/// is gone while its replacement sits live in the graph answering the same question.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class LegibleForgettingIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    public LegibleForgettingIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static float[] Vector(float lead) => [lead, 0.1f, 0.1f, 0.1f];

    private static Fact Make(string @object, float[] embedding, string owner = "alice") => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "flights",
        Predicate = "prefers",
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-120),
        OwnerId = owner,
        Embedding = embedding,
    };

    /// <summary>Soft-invalidates by the same route the prune does, so the reason is stamped.</summary>
    private Task<int> PruneAsync() =>
        _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            // The REAL prune query with the REAL parameter names, taken from
            // Neo4jMemoryDecayService's call site rather than invented -- a fixture that prunes by a
            // hand-written approximation of the query would prove nothing about the query that ships.
            // minScore is set above 1.0 so every fact falls below it and is pruned.
            var cypher = DecayQueries.PruneFacts(hasOwnerFilter: false, nonDestructive: true);
            var cursor = await runner.RunAsync(cypher, new Dictionary<string, object?>
            {
                ["now"] = DateTimeOffset.UtcNow.ToString("O"),
                ["lambda"] = 1.0,
                ["boostFactor"] = 0.0,
                ["maxBoost"] = 0.0,
                ["minScore"] = 2.0,
            });
            var records = await cursor.ToListAsync();
            return records.Count;
        }, CancellationToken.None);

    private async Task<string?> ReasonOfAsync(string factId) =>
        await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(
                "MATCH (f:Fact {id: $id}) RETURN f.invalidated_reason AS reason", new { id = factId });
            var records = await cursor.ToListAsync();
            return records.Count == 0 ? null : records[0]["reason"]?.As<string>();
        }, CancellationToken.None);

    // ── the write side ────────────────────────────────────────────────

    [Fact]
    public async Task APrunedFactCarriesTheDecayReason()
    {
        var fact = await _facts.UpsertAsync(Make("the aisle seat", Vector(0.9f)));

        await PruneAsync();

        (await ReasonOfAsync(fact.FactId)).Should().Be("decay");
    }

    [Fact]
    public async Task ASupersededFactCarriesNoReason()
    {
        // The partition. Superseded is not decayed, and a null reason is what says so.
        var loser = await _facts.UpsertAsync(Make("the aisle seat", Vector(0.9f)));
        var winner = await _facts.UpsertAsync(Make("the window seat", Vector(0.8f)));

        await _facts.SupersedeAsync(loser.FactId, winner.FactId, Alice);

        (await ReasonOfAsync(loser.FactId)).Should().BeNull();
    }

    [Fact]
    public async Task AnInvalidatedFactCarriesNoReasonEither()
    {
        var fact = await _facts.UpsertAsync(Make("the aisle seat", Vector(0.9f)));

        await _facts.InvalidateAsync(fact.FactId, Alice);

        (await ReasonOfAsync(fact.FactId)).Should().BeNull();
    }

    [Fact]
    public async Task ARepeatedPruneDoesNotRestampAnAlreadyDecayedFact()
    {
        var fact = await _facts.UpsertAsync(Make("the aisle seat", Vector(0.9f)));
        await PruneAsync();
        var firstReason = await ReasonOfAsync(fact.FactId);

        await PruneAsync();

        (await ReasonOfAsync(fact.FactId)).Should().Be(firstReason);
    }

    // ── the read side ─────────────────────────────────────────────────

    [Fact]
    public async Task TheProbeReturnsOnlyTheDecayedFact()
    {
        // Three facts sharing a subject and a similar vector, in three different states. Only one of
        // them is something the system forgot.
        var decayed = await _facts.UpsertAsync(Make("the aisle seat", Vector(0.9f)));
        await PruneAsync();

        var live = await _facts.UpsertAsync(Make("extra legroom", Vector(0.9f)));
        var superseded = await _facts.UpsertAsync(Make("the middle seat", Vector(0.9f)));
        await _facts.SupersedeAsync(superseded.FactId, live.FactId, Alice);

        var hits = await _facts.SearchDecayedFactsAsync(Vector(0.9f), 10, 0.0, Alice);

        hits.Select(f => f.FactId).Should().Equal(decayed.FactId);
    }

    [Fact]
    public async Task TheProbeReadsBackTheReason()
    {
        await _facts.UpsertAsync(Make("the aisle seat", Vector(0.9f)));
        await PruneAsync();

        var hits = await _facts.SearchDecayedFactsAsync(Vector(0.9f), 10, 0.0, Alice);

        hits.Should().ContainSingle().Which.InvalidatedReason.Should().Be("decay");
    }

    [Fact]
    public async Task TheProbeIsOwnerIsolated()
    {
        await _facts.UpsertAsync(Make("mine", Vector(0.9f), owner: "alice"));
        await _facts.UpsertAsync(Make("theirs", Vector(0.9f), owner: "bob"));
        await PruneAsync();

        var hits = await _facts.SearchDecayedFactsAsync(Vector(0.9f), 10, 0.0, Alice);

        hits.Should().ContainSingle().Which.Object.Should().Be("mine");
    }

    [Fact]
    public async Task AnEmptyEmbeddingReturnsNothingRatherThanThrowing()
    {
        // The boundary invariant the empty-embedding sweep pinned across all nine vector searches; this
        // is the tenth and obeys it too.
        var hits = await _facts.SearchDecayedFactsAsync([], 10, 0.0, Alice);

        hits.Should().BeEmpty();
    }

    [Fact]
    public async Task NothingDecayedYieldsNoHits()
    {
        await _facts.UpsertAsync(Make("the aisle seat", Vector(0.9f)));

        var hits = await _facts.SearchDecayedFactsAsync(Vector(0.9f), 10, 0.0, Alice);

        hits.Should().BeEmpty();
    }
}
