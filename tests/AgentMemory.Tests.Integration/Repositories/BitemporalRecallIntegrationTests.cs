using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// D6 bitemporal fact recall against live Neo4j: <c>SearchByVectorAsOfAsync</c> binds TWO independent
/// clocks — the valid-time clock (<c>validAsOf</c> → <c>valid_from</c>/<c>valid_until</c>, "what was
/// true") and the transaction-time clock (<c>systemAsOf</c> → <c>created_at</c>/<c>invalidated_at</c>,
/// "what the system believed"). These tests pin all four timestamps and prove each clock filters
/// independently of the other — the scenario the single-clock <c>asOf</c> path can never exercise.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class BitemporalRecallIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jPreferenceRepository _prefs;

    private static readonly float[] Emb = [0.5f, 0.5f, 0.5f, 0.5f];

    // A fact with a fully-pinned bitemporal footprint:
    //   recorded (created_at) ──── true in the world [valid_from, valid_until) ──── stopped believing (invalidated_at)
    private static readonly DateTimeOffset CreatedAt     = new(2025, 1, 1, 0, 0, 0, TimeSpan.Zero); // T0
    private static readonly DateTimeOffset ValidFrom     = new(2025, 2, 1, 0, 0, 0, TimeSpan.Zero); // V0
    private static readonly DateTimeOffset ValidUntil    = new(2025, 4, 1, 0, 0, 0, TimeSpan.Zero); // V1
    private static readonly DateTimeOffset InvalidatedAt = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero); // T2

    public BitemporalRecallIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _prefs = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SearchByVectorAsOf_ValidAndSystemClocks_FilterIndependently()
    {
        await SeedPinnedFactAsync();

        // Case 1 — true at validAsOf AND still believed at systemAsOf → RETURNED.
        // validAsOf 2025-03-01 ∈ [02-01, 04-01); systemAsOf 2025-05-01 ∈ [created 01-01, invalidated 06-01).
        (await _facts.SearchByVectorAsOfAsync(
                Emb, asOf: D(2025, 3, 1), limit: 5, systemAsOf: D(2025, 5, 1)))
            .Should().ContainSingle(r => r.Fact.FactId == "bt1",
                "the fact was both true in the world and believed by the system at these instants");

        // Case 2 — same valid-time, but AFTER the system stopped believing it → EXCLUDED by transaction clock only.
        // validAsOf still inside the window; systemAsOf 2025-07-01 > invalidated_at 06-01.
        (await _facts.SearchByVectorAsOfAsync(
                Emb, asOf: D(2025, 3, 1), limit: 5, systemAsOf: D(2025, 7, 1)))
            .Should().NotContain(r => r.Fact.FactId == "bt1",
                "the valid clock still includes it, so only the transaction clock can be excluding it here");

        // Case 3 — believed by the system, but AFTER it ceased to be true → EXCLUDED by valid clock only.
        // systemAsOf 2025-05-01 still before invalidation; validAsOf 2025-05-01 > valid_until 04-01.
        (await _facts.SearchByVectorAsOfAsync(
                Emb, asOf: D(2025, 5, 1), limit: 5, systemAsOf: D(2025, 5, 1)))
            .Should().NotContain(r => r.Fact.FactId == "bt1",
                "the transaction clock still includes it, so only the valid clock can be excluding it here");
    }

    [Fact]
    public async Task SearchByVectorAsOf_SingleClock_CollapsesBothToTheSameInstant()
    {
        await SeedPinnedFactAsync();

        // Single-clock recall (systemAsOf defaults to asOf): a point where the fact is both true AND
        // believed (2025-03-01 ∈ valid window and before invalidation) returns it...
        (await _facts.SearchByVectorAsOfAsync(Emb, asOf: D(2025, 3, 1), limit: 5))
            .Should().ContainSingle(r => r.Fact.FactId == "bt1");

        // ...and a point after the valid window (2025-05-01) excludes it on the single shared clock.
        (await _facts.SearchByVectorAsOfAsync(Emb, asOf: D(2025, 5, 1), limit: 5))
            .Should().NotContain(r => r.Fact.FactId == "bt1");
    }

    [Fact]
    public async Task SearchEntitiesAsOf_TransactionClock_ExcludesAfterInvalidation_IncludesBefore()
    {
        // Entities have only the transaction clock: the AsOf timestamp binds created_at/invalidated_at.
        await _entities.UpsertAsync(new Entity
        {
            EntityId = "e1", Name = "Rome", Type = "PLACE", Confidence = 0.9, Embedding = Emb,
            CreatedAtUtc = CreatedAt,
        });
        await PinClockAsync("Entity", "e1");

        (await _entities.SearchByVectorAsOfAsync(Emb, D(2025, 3, 1), limit: 5))
            .Should().ContainSingle(r => r.Entity.EntityId == "e1", "before invalidation the system believed it");
        (await _entities.SearchByVectorAsOfAsync(Emb, D(2025, 7, 1), limit: 5))
            .Should().NotContain(r => r.Entity.EntityId == "e1", "after invalidation the system no longer believed it");
    }

    [Fact]
    public async Task SearchPreferencesAsOf_TransactionClock_ExcludesAfterInvalidation_IncludesBefore()
    {
        await _prefs.UpsertAsync(new Preference
        {
            PreferenceId = "pr1", Category = "style", PreferenceText = "dark mode", Confidence = 0.9, Embedding = Emb,
            CreatedAtUtc = CreatedAt,
        });
        await PinClockAsync("Preference", "pr1");

        (await _prefs.SearchByVectorAsOfAsync(Emb, D(2025, 3, 1), limit: 5))
            .Should().ContainSingle(r => r.Preference.PreferenceId == "pr1", "before invalidation the system believed it");
        (await _prefs.SearchByVectorAsOfAsync(Emb, D(2025, 7, 1), limit: 5))
            .Should().NotContain(r => r.Preference.PreferenceId == "pr1", "after invalidation the system no longer believed it");
    }

    private static DateTimeOffset D(int year, int month, int day) =>
        new(year, month, day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Pins created_at + invalidated_at on a node so the transaction-clock boundary is exact.</summary>
    private async Task PinClockAsync(string label, string id)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            $"MATCH (n:{label} {{id: $id}}) SET n.created_at = datetime($createdAt), n.invalidated_at = datetime($invalidatedAt)",
            new
            {
                id,
                createdAt = CreatedAt.UtcDateTime.ToString("O"),
                invalidatedAt = InvalidatedAt.UtcDateTime.ToString("O"),
            });
    }

    /// <summary>
    /// Upserts the fact (so the vector index is populated by the repo), then pins all four bitemporal
    /// timestamps via raw Cypher so the two-clock boundaries are exact and deterministic — independent of
    /// wall-clock and of how the repo stamps defaults.
    /// </summary>
    private async Task SeedPinnedFactAsync()
    {
        await _facts.UpsertAsync(new Fact
        {
            FactId = "bt1", Subject = "rome", Predicate = "is_capital_of", Object = "empire",
            Confidence = 0.9, Embedding = Emb,
            CreatedAtUtc = CreatedAt, ValidFrom = ValidFrom, ValidUntil = ValidUntil,
        });

        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            @"MATCH (f:Fact {id: $id})
              SET f.created_at = datetime($createdAt),
                  f.valid_from = datetime($validFrom),
                  f.valid_until = datetime($validUntil),
                  f.invalidated_at = datetime($invalidatedAt)",
            new
            {
                id = "bt1",
                createdAt = CreatedAt.UtcDateTime.ToString("O"),
                validFrom = ValidFrom.UtcDateTime.ToString("O"),
                validUntil = ValidUntil.UtcDateTime.ToString("O"),
                invalidatedAt = InvalidatedAt.UtcDateTime.ToString("O"),
            });
    }
}
