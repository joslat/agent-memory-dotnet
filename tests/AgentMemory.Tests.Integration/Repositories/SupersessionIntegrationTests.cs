using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// D7 supersession against live Neo4j: the <c>SupersedeAsync</c> writer closes a loser non-destructively
/// (drops it from live recall but keeps it for as-of recall before supersession) and links
/// <c>:SUPERSEDED_BY</c>; the opt-in contradiction resolver keeps the highest-confidence assertion and
/// supersedes the rest; and the duplicate-preference collapse is non-destructive and idempotent. All are
/// owner-scoped (R1).
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class SupersessionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jPreferenceRepository _prefs;
    private readonly Neo4jConflictDetectionService _conflicts;

    private static readonly float[] Emb = [0.5f, 0.5f, 0.5f, 0.5f];

    public SupersessionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _prefs = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 13, 0, 0, 0, TimeSpan.Zero));
        _conflicts = new Neo4jConflictDetectionService(
            fixture.TransactionRunner, clock, NullLogger<Neo4jConflictDetectionService>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // ── SupersedeAsync (the writer) ──────────────────────────────────────

    [Fact]
    public async Task SupersedeAsync_DropsLoserFromLiveRecall_KeepsAsOfBefore_AndLinksWinner()
    {
        await _facts.UpsertAsync(new Fact
        {
            FactId = "f-loser", Subject = "sky", Predicate = "is", Object = "blue",
            Confidence = 0.7, Embedding = Emb, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-10),
        });
        await _facts.UpsertAsync(new Fact
        {
            FactId = "f-winner", Subject = "sky", Predicate = "is", Object = "grey",
            Confidence = 0.9, Embedding = Emb, CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });

        var asOfBefore = DateTimeOffset.UtcNow.AddDays(-5);

        (await _facts.SupersedeAsync("f-loser", "f-winner")).Should().BeTrue();

        // Live recall now excludes the superseded loser but keeps the winner.
        var live = await _facts.SearchByVectorAsync(Emb, limit: 5);
        live.Should().NotContain(r => r.Fact.FactId == "f-loser", "the superseded loser drops from live recall");
        live.Should().Contain(r => r.Fact.FactId == "f-winner");

        // As-of a time before supersession the loser is still recalled (nothing was deleted).
        (await _facts.SearchByVectorAsOfAsync(Emb, asOfBefore, limit: 5))
            .Should().Contain(r => r.Fact.FactId == "f-loser",
                "the loser was still believed at the earlier time");

        // The supersession edge points loser → winner.
        (await ScalarAsync(
            "MATCH (l:Fact {id:'f-loser'})-[:SUPERSEDED_BY]->(w:Fact {id:'f-winner'}) RETURN count(*) AS c"))
            .Should().Be(1);
    }

    [Fact]
    public async Task SupersedeAsync_IsOwnerScoped_CannotSupersedeAnotherOwnersFacts()
    {
        await _facts.UpsertAsync(new Fact
        {
            FactId = "fb-loser", Subject = "bob", Predicate = "likes", Object = "x",
            Confidence = 0.7, Embedding = Emb, OwnerId = "bob", CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-2),
        });
        await _facts.UpsertAsync(new Fact
        {
            FactId = "fb-winner", Subject = "bob", Predicate = "likes", Object = "y",
            Confidence = 0.9, Embedding = Emb, OwnerId = "bob", CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });

        // Alice tries to supersede Bob's facts — must not match.
        (await _facts.SupersedeAsync("fb-loser", "fb-winner", MemoryScope.For("alice")))
            .Should().BeFalse("a scoped supersede must not touch another owner's facts");

        // Bob's loser is untouched (no invalidated_at, no edge) and still live.
        (await ScalarAsync("MATCH (f:Fact {id:'fb-loser'}) WHERE f.invalidated_at IS NULL RETURN count(f) AS c"))
            .Should().Be(1);
        (await ScalarAsync("MATCH (:Fact {id:'fb-loser'})-[:SUPERSEDED_BY]->() RETURN count(*) AS c"))
            .Should().Be(0);
    }

    // ── ResolveFactContradictionsAsync (opt-in resolver) ─────────────────

    [Fact]
    public async Task ResolveFactContradictionsAsync_KeepsHighestConfidence_SupersedesLosers()
    {
        // Alice (owner-scoped) asserts two objects for the same subject+predicate — a contradiction.
        await SeedFactAsync("a-acme", "Alice", "works_at", "Acme", owner: "alice", confidence: 0.9);
        await SeedFactAsync("a-globex", "Alice", "works_at", "Globex", owner: "alice", confidence: 0.6);

        var result = await _conflicts.ResolveFactContradictionsAsync();

        result.ConflictsResolved.Should().Be(1);
        result.FactsSuperseded.Should().Be(1);

        // Winner (higher confidence) stays live; loser is superseded (invalidated), not deleted.
        (await ScalarAsync("MATCH (f:Fact {id:'a-acme'}) WHERE f.invalidated_at IS NULL RETURN count(f) AS c"))
            .Should().Be(1, "the highest-confidence assertion wins and stays live");
        (await ScalarAsync("MATCH (f:Fact {id:'a-globex'}) WHERE f.invalidated_at IS NOT NULL RETURN count(f) AS c"))
            .Should().Be(1, "the loser is superseded (kept, but invalidated)");
        (await ScalarAsync(
            "MATCH (:Fact {id:'a-globex'})-[:SUPERSEDED_BY]->(:Fact {id:'a-acme'}) RETURN count(*) AS c"))
            .Should().Be(1);
    }

    [Fact]
    public async Task ResolveFactContradictionsAsync_RespectsOwnerIsolation()
    {
        // Two different owners each assert a single (different) object — NOT a contradiction across owners.
        await SeedFactAsync("alice-f", "City", "capital_is", "Paris", owner: "alice", confidence: 0.9);
        await SeedFactAsync("bob-f", "City", "capital_is", "Berlin", owner: "bob", confidence: 0.9);

        var result = await _conflicts.ResolveFactContradictionsAsync();

        result.ConflictsResolved.Should().Be(0, "facts of different owners must not be treated as contradicting");
        (await ScalarAsync("MATCH (f:Fact) WHERE f.invalidated_at IS NOT NULL RETURN count(f) AS c"))
            .Should().Be(0, "nothing should be superseded across the owner boundary");
    }

    // ── Non-destructive duplicate-preference collapse ────────────────────

    [Fact]
    public async Task RemoveDuplicatePreferences_NonDestructive_SoftInvalidatesAndIsIdempotent()
    {
        await SeedPreferenceAsync("p-old", "u1", "style", "dark mode", DateTimeOffset.UtcNow.AddDays(-2));
        await SeedPreferenceAsync("p-new", "u1", "style", "dark mode", DateTimeOffset.UtcNow.AddDays(-1));

        // First collapse: one duplicate (the older) is closed.
        (await ScalarAsync(ConsolidationQueries.RemoveDuplicatePreferences, new { minGroupSize = 2 }))
            .Should().Be(1);

        // The older duplicate is KEPT (not deleted) and soft-invalidated; the newer stays live.
        (await ScalarAsync("MATCH (p:Preference {id:'p-old'}) WHERE p.invalidated_at IS NOT NULL RETURN count(p) AS c"))
            .Should().Be(1, "the older duplicate is soft-invalidated, not deleted");
        (await ScalarAsync("MATCH (p:Preference {id:'p-new'}) WHERE p.invalidated_at IS NULL RETURN count(p) AS c"))
            .Should().Be(1, "the newest is kept live");
        (await ScalarAsync("MATCH (:Preference {id:'p-old'})-[:SUPERSEDED_BY]->(:Preference {id:'p-new'}) RETURN count(*) AS c"))
            .Should().Be(1);

        // Idempotent: a re-run finds nothing new (already-invalidated rows are excluded from grouping).
        (await ScalarAsync(ConsolidationQueries.RemoveDuplicatePreferences, new { minGroupSize = 2 }))
            .Should().Be(0, "a second collapse must be a no-op");
    }

    [Fact]
    public async Task ResolveFactContradictionsAsync_IsIdempotent_SecondPassIsNoOp()
    {
        await SeedFactAsync("a-acme", "Alice", "works_at", "Acme", owner: "alice", confidence: 0.9);
        await SeedFactAsync("a-globex", "Alice", "works_at", "Globex", owner: "alice", confidence: 0.6);

        var first = await _conflicts.ResolveFactContradictionsAsync();
        first.ConflictsResolved.Should().Be(1);
        first.FactsSuperseded.Should().Be(1);

        // Second pass: the loser is now invalidated and excluded from (live-only) detection, so the group
        // collapses to a single live object — nothing to resolve, no fictitious counts.
        var second = await _conflicts.ResolveFactContradictionsAsync();
        second.ConflictsResolved.Should().Be(0, "re-running resolution must be a no-op");
        second.FactsSuperseded.Should().Be(0);
    }

    [Fact]
    public async Task ResolveFactContradictionsAsync_IgnoresDeadFacts_NeverSupersedesLiveWithAnInvalidatedWinner()
    {
        // A higher-confidence fact was already retired; the live (lower-confidence) fact is the truth now.
        await SeedFactAsync("dead-hi", "Alice", "lives_in", "Oldtown", owner: "alice", confidence: 0.95);
        await SeedFactAsync("live-lo", "Alice", "lives_in", "Newtown", owner: "alice", confidence: 0.80);
        (await _facts.InvalidateAsync("dead-hi", MemoryScope.For("alice"))).Should().BeTrue();

        var result = await _conflicts.ResolveFactContradictionsAsync();

        // Only one LIVE object remains → no live contradiction → the live fact is never superseded by a dead one.
        result.ConflictsResolved.Should().Be(0);
        (await ScalarAsync("MATCH (f:Fact {id:'live-lo'}) WHERE f.invalidated_at IS NULL RETURN count(f) AS c"))
            .Should().Be(1, "a live fact must never be superseded by an already-invalidated one");
    }

    [Fact]
    public async Task ResolveFactContradictionsAsync_WinnerFloor_SkipsGroupsWhoseBestAssertionIsWeak()
    {
        // Both assertions are weak; the winner (0.5) is below the floor → group left unresolved.
        await SeedFactAsync("w-a", "Sky", "is", "blue", owner: "alice", confidence: 0.5);
        await SeedFactAsync("w-b", "Sky", "is", "green", owner: "alice", confidence: 0.4);

        var result = await _conflicts.ResolveFactContradictionsAsync(
            new ConflictResolutionOptions { MinConfidence = 0.7 });

        result.ConflictsResolved.Should().Be(0, "no group whose best assertion is below the floor is resolved");
        (await ScalarAsync("MATCH (f:Fact) WHERE f.invalidated_at IS NOT NULL RETURN count(f) AS c")).Should().Be(0);
    }

    [Fact]
    public async Task SupersedeAsync_Unscoped_CannotLinkAcrossOwners()
    {
        await SeedFactAsync("x-alice", "k", "v", "a", owner: "alice", confidence: 0.7);
        await SeedFactAsync("x-bob", "k", "v", "b", owner: "bob", confidence: 0.9);

        // Even with NO scope (admin path), a cross-owner supersede must not happen.
        (await _facts.SupersedeAsync("x-alice", "x-bob"))
            .Should().BeFalse("supersession must never link one owner's fact to another owner's");
        (await ScalarAsync("MATCH (f:Fact {id:'x-alice'}) WHERE f.invalidated_at IS NULL RETURN count(f) AS c"))
            .Should().Be(1, "the cross-owner loser must remain live");
        (await ScalarAsync("MATCH (:Fact {id:'x-alice'})-[:SUPERSEDED_BY]->() RETURN count(*) AS c"))
            .Should().Be(0, "no cross-owner supersession edge may exist");
    }

    [Fact]
    public async Task SupersedeAsync_Scoped_CannotSupersedeWhenWinnerBelongsToAnotherOwner()
    {
        await SeedFactAsync("s-loser", "k", "v", "a", owner: "alice", confidence: 0.7);
        await SeedFactAsync("s-winner", "k", "v", "b", owner: "bob", confidence: 0.9);

        // Scoped to alice: the winner (bob's) is out of scope → winner MATCH fails → false, nothing changes.
        (await _facts.SupersedeAsync("s-loser", "s-winner", MemoryScope.For("alice")))
            .Should().BeFalse();
        (await ScalarAsync("MATCH (f:Fact {id:'s-loser'}) WHERE f.invalidated_at IS NULL RETURN count(f) AS c"))
            .Should().Be(1);
        (await ScalarAsync("MATCH (:Fact {id:'s-loser'})-[:SUPERSEDED_BY]->() RETURN count(*) AS c")).Should().Be(0);
    }

    // ── Preference supersession (live) ───────────────────────────────────

    [Fact]
    public async Task PreferenceSupersedeAsync_DropsLoserFromLiveRecall_KeepsAsOfBefore_AndLinksWinner()
    {
        await SeedPreferenceAsync("pl", "u1", "tone", "formal", DateTimeOffset.UtcNow.AddDays(-10));
        await SeedPreferenceAsync("pw", "u1", "tone", "casual", DateTimeOffset.UtcNow.AddDays(-1));

        var asOfBefore = DateTimeOffset.UtcNow.AddDays(-5);

        (await _prefs.SupersedeAsync("pl", "pw", MemoryScope.For("u1"))).Should().BeTrue();

        var live = await _prefs.SearchByVectorAsync(Emb, limit: 5);
        live.Should().NotContain(r => r.Preference.PreferenceId == "pl", "the superseded preference drops from live recall");
        live.Should().Contain(r => r.Preference.PreferenceId == "pw");

        (await _prefs.SearchByVectorAsOfAsync(Emb, asOfBefore, limit: 5))
            .Should().Contain(r => r.Preference.PreferenceId == "pl",
                "as-of before supersession the loser is still recalled");

        (await ScalarAsync("MATCH (:Preference {id:'pl'})-[:SUPERSEDED_BY]->(:Preference {id:'pw'}) RETURN count(*) AS c"))
            .Should().Be(1);
    }

    [Fact]
    public async Task PreferenceSupersedeAsync_IsOwnerScoped_CannotTouchAnotherOwner()
    {
        await SeedPreferenceAsync("pb-loser", "bob", "tone", "formal", DateTimeOffset.UtcNow.AddDays(-2));
        await SeedPreferenceAsync("pb-winner", "bob", "tone", "casual", DateTimeOffset.UtcNow.AddDays(-1));

        (await _prefs.SupersedeAsync("pb-loser", "pb-winner", MemoryScope.For("alice")))
            .Should().BeFalse("a scoped supersede must not touch another owner's preferences");
        (await ScalarAsync("MATCH (p:Preference {id:'pb-loser'}) WHERE p.invalidated_at IS NULL RETURN count(p) AS c"))
            .Should().Be(1);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Task SeedFactAsync(string id, string subject, string predicate, string obj, string? owner, double confidence) =>
        _facts.UpsertAsync(new Fact
        {
            FactId = id, Subject = subject, Predicate = predicate, Object = obj,
            OwnerId = owner, Confidence = confidence, Embedding = Emb,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-1),
        });

    private Task SeedPreferenceAsync(string id, string owner, string category, string text, DateTimeOffset created) =>
        _prefs.UpsertAsync(new Preference
        {
            PreferenceId = id, OwnerId = owner, Category = category, PreferenceText = text,
            Confidence = 0.8, Embedding = Emb, CreatedAtUtc = created,
        });

    private async Task<long> ScalarAsync(string cypher, object? parameters = null)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher, parameters ?? new { });
        var record = await cursor.SingleAsync();
        // Read by ordinal so this works for any single-column projection (AS c / AS count / …).
        return global::Neo4j.Driver.ValueExtensions.As<long>(record[0]);
    }
}
