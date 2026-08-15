using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 30.5. Delta recall against a live graph: every change appears in <b>exactly one</b> bucket.
/// </summary>
/// <remarks>
/// <para>
/// Exactly-once is not a nice property here, it is the whole feature. Consecutive deltas partition
/// time by construction — strictly <c>&gt; since</c>, inclusively <c>&lt;= until</c>, everywhere — and
/// that is what lets the feature be verified without a judge or a benchmark. One <c>&gt;=</c> where
/// <c>&gt;</c> belongs silently duplicates an item across windows, or drops it.
/// </para>
/// <para>
/// The subtlest case has its own test: supersession stamps <b>both</b> clocks, so a superseded fact
/// would appear as a pair AND under expired-validity unless the transaction-clock gate holds.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class DeltaRecallIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jPreferenceRepository _preferences;
    private readonly Neo4jEntityRepository _entities;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    public DeltaRecallIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _preferences = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Fact NewFact(
        string @object, string owner = "alice",
        DateTimeOffset? createdAt = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validUntil = null) => new()
    {
        FactId = Guid.NewGuid().ToString("N"),
        Subject = "user",
        Predicate = "works_at",
        Object = @object,
        Confidence = 0.9,
        CreatedAtUtc = createdAt ?? DateTimeOffset.UtcNow,
        OwnerId = owner,
        ValidFrom = validFrom,
        ValidUntil = validUntil,
    };

    private Task<FactDeltaRows> DeltaAsync(DateTimeOffset since, MemoryScope? scope = null, int cap = 20) =>
        _facts.ListChangedInWindowAsync(since, DateTimeOffset.UtcNow.AddMinutes(1), scope ?? Alice, cap);

    // ── membership ────────────────────────────────────────────────────

    [Fact]
    public async Task ANewFactAppearsOnlyInNewFacts()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _facts.UpsertAsync(NewFact("Acme"));

        var delta = await DeltaAsync(since);

        delta.NewFacts.Should().ContainSingle().Which.Object.Should().Be("Acme");
        delta.SupersededPairs.Should().BeEmpty();
        delta.InvalidatedFacts.Should().BeEmpty();
        delta.ExpiredValidity.Should().BeEmpty();
        delta.NewlyDueProspective.Should().BeEmpty();
    }

    [Fact]
    public async Task ASupersededFactAppearsONLYAsAPairAndNotAlsoAsExpiredValidity()
    {
        // THE subtle one. Supersede stamps invalidated_at AND valid_until, so without the
        // transaction-clock gate on the expired-validity bucket this fact would appear twice and the
        // exactly-once invariant the whole feature rests on would be quietly false.
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        var loser = await _facts.UpsertAsync(NewFact("Acme"));
        var winner = await _facts.UpsertAsync(NewFact("Globex"));
        await _facts.SupersedeAsync(loser.FactId, winner.FactId, Alice);

        var delta = await DeltaAsync(since);

        delta.SupersededPairs.Should().ContainSingle();
        delta.SupersededPairs[0].Old.Object.Should().Be("Acme");
        delta.SupersededPairs[0].New.Object.Should().Be("Globex");
        delta.ExpiredValidity.Should().BeEmpty("a superseded fact is a pair, not an expiry");
        delta.InvalidatedFacts.Should().BeEmpty("it has a successor, so it was replaced, not retracted");
        delta.NewFacts.Should().ContainSingle().Which.Object.Should().Be("Globex");
    }

    [Fact]
    public async Task AnInvalidatedFactWithNoSuccessorIsRetractedNotReplaced()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        var fact = await _facts.UpsertAsync(NewFact("Acme"));
        await _facts.InvalidateAsync(fact.FactId, Alice);

        var delta = await DeltaAsync(since);

        delta.InvalidatedFacts.Should().ContainSingle().Which.Object.Should().Be("Acme");
        delta.SupersededPairs.Should().BeEmpty();
        delta.NewFacts.Should().BeEmpty("it is no longer live");
    }

    [Fact]
    public async Task AFactWhoseValidityClosedInTheWindowIsAnExpiry()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _facts.UpsertAsync(NewFact("Acme", validUntil: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var delta = await DeltaAsync(since);

        delta.ExpiredValidity.Should().ContainSingle().Which.Object.Should().Be("Acme");
        delta.InvalidatedFacts.Should().BeEmpty("it is still live on the transaction clock");
    }

    [Fact]
    public async Task AFactKnownBeforeTheWindowThatBecameDueInsideItIsNewlyDue()
    {
        // created BEFORE the window, valid_from INSIDE it -- the prospective case.
        var created = DateTimeOffset.UtcNow.AddHours(-2);
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _facts.UpsertAsync(NewFact(
            "Acme", createdAt: created, validFrom: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var delta = await DeltaAsync(since);

        delta.NewlyDueProspective.Should().ContainSingle().Which.Object.Should().Be("Acme");
        delta.NewFacts.Should().BeEmpty("it was created before the window");
    }

    [Fact]
    public async Task AFactBothCreatedAndBecomingDueInTheWindowIsReportedAsNewOnly()
    {
        // Disjointness: "new" is the more informative of the two, and reporting both would double-count.
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _facts.UpsertAsync(NewFact("Acme", validFrom: DateTimeOffset.UtcNow.AddSeconds(-1)));

        var delta = await DeltaAsync(since);

        delta.NewFacts.Should().ContainSingle();
        delta.NewlyDueProspective.Should().BeEmpty();
    }

    // ── boundaries ────────────────────────────────────────────────────

    [Fact]
    public async Task TheWindowIsHalfOpenSoAFactAtSinceIsExcluded()
    {
        // created_at == since must be EXCLUDED, or it appears in two consecutive deltas.
        var at = DateTimeOffset.UtcNow.AddMinutes(-3);
        await _facts.UpsertAsync(NewFact("Acme", createdAt: at));

        var delta = await _facts.ListChangedInWindowAsync(at, DateTimeOffset.UtcNow, Alice, 20);

        delta.NewFacts.Should().BeEmpty("strictly greater than since, by convention, everywhere");
    }

    [Fact]
    public async Task AFactAtUntilIsIncluded()
    {
        // created_at == until must be INCLUDED, or it falls between two consecutive deltas and is lost.
        var at = DateTimeOffset.UtcNow.AddMinutes(-3);
        await _facts.UpsertAsync(NewFact("Acme", createdAt: at));

        var delta = await _facts.ListChangedInWindowAsync(at.AddMinutes(-1), at, Alice, 20);

        delta.NewFacts.Should().ContainSingle();
    }

    [Fact]
    public async Task ConsecutiveDeltasPartitionTimeExactly()
    {
        // The property the whole feature rests on, exercised end to end: nothing appears twice and
        // nothing is lost across a checkpoint handover.
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-10);
        var first = await _facts.UpsertAsync(NewFact("Acme", createdAt: DateTimeOffset.UtcNow.AddMinutes(-8)));
        var checkpoint = DateTimeOffset.UtcNow.AddMinutes(-5);
        var second = await _facts.UpsertAsync(NewFact("Globex", createdAt: DateTimeOffset.UtcNow.AddMinutes(-2)));

        var window1 = await _facts.ListChangedInWindowAsync(t0, checkpoint, Alice, 20);
        var window2 = await _facts.ListChangedInWindowAsync(checkpoint, DateTimeOffset.UtcNow, Alice, 20);

        window1.NewFacts.Select(f => f.FactId).Should().Equal(first.FactId);
        window2.NewFacts.Select(f => f.FactId).Should().Equal(second.FactId);
    }

    // ── isolation and caps ────────────────────────────────────────────

    [Fact]
    public async Task AnotherOwnersChangesNeverAppear()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _facts.UpsertAsync(NewFact("Acme", owner: "alice"));
        await _facts.UpsertAsync(NewFact("Globex", owner: "bob"));

        var delta = await DeltaAsync(since);

        delta.NewFacts.Should().ContainSingle().Which.Object.Should().Be("Acme");
    }

    [Fact]
    public async Task ThePerBucketCapIsHonoured()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        for (var i = 0; i < 5; i++)
            await _facts.UpsertAsync(NewFact($"Company{i}"));

        var delta = await DeltaAsync(since, cap: 2);

        delta.NewFacts.Should().HaveCount(2);
    }

    [Fact]
    public async Task NothingChangedYieldsEveryBucketEmpty()
    {
        var delta = await DeltaAsync(DateTimeOffset.UtcNow.AddSeconds(-1));

        delta.NewFacts.Should().BeEmpty();
        delta.SupersededPairs.Should().BeEmpty();
        delta.InvalidatedFacts.Should().BeEmpty();
    }

    // ── preferences and entities ──────────────────────────────────────

    [Fact]
    public async Task NewPreferencesAndEntitiesAreReported()
    {
        var since = DateTimeOffset.UtcNow.AddMinutes(-5);
        await _preferences.UpsertAsync(new Preference
        {
            PreferenceId = Guid.NewGuid().ToString("N"),
            Category = "food", PreferenceText = "vegetarian", Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = "alice",
        });
        await _entities.UpsertAsync(new Entity
        {
            EntityId = Guid.NewGuid().ToString("N"),
            Name = "Acme", Type = "ORGANIZATION", Confidence = 0.9,
            CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = "alice",
        });

        var prefs = await _preferences.ListChangedInWindowAsync(since, DateTimeOffset.UtcNow.AddMinutes(1), Alice, 20);
        var ents = await _entities.ListCreatedInWindowAsync(since, DateTimeOffset.UtcNow.AddMinutes(1), Alice, 20);

        prefs.NewPreferences.Should().ContainSingle();
        ents.Should().ContainSingle().Which.Name.Should().Be("Acme");
    }
}
