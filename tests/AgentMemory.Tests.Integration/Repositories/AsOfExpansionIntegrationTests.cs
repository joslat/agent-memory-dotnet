using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Memory;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// W1c against live Neo4j: predicate expansion inside point-in-time recall must return a relation
/// whole <b>as it stood at that instant</b>, not as it stands now.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests for this feature assert at the service seam over substitutes, so they prove the
/// clocks are <i>passed</i>. They cannot prove the Cypher <i>uses</i> them — or that it parses at
/// all. Without this file the query's first execution would be inside a multi-hour paid run.
/// </para>
/// <para>
/// The failure this guards is asymmetric and that is why it needs a live store: a dropped clock in
/// expansion does not lose a row, it <b>adds</b> one. The extra fact looks exactly like legitimate
/// relation completeness, so nothing downstream can tell it apart from a correct answer.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class AsOfExpansionIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly float[] Emb = [0.5f, 0.5f, 0.5f, 0.5f];
    private const string Predicate = "paid_for";

    /// <summary>
    /// Expansion matches <c>predicate_key</c>, never the raw predicate — that is the whole point of
    /// canonical identity. Querying with the raw string returned nothing from BOTH the as-of and the
    /// unbounded query on first run, which is exactly what the control test below exists to reveal:
    /// an empty as-of result meant a broken test, not a working filter.
    /// </summary>
    private static readonly string PredicateKey = MemoryTripleCanonicalizer.Canonical(Predicate);
    private const string Owner = "owner-w1c";

    public AsOfExpansionIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The invariant, end to end: expansion returns only what was true and believed then.</summary>
    [Fact]
    public async Task ExpansionAsOf_ReturnsTheRelationAsItStoodAtThatInstant()
    {
        // Three facts under ONE predicate, so expansion matches all three by predicate_key and only
        // the clocks can separate them.
        await SeedAsync("in-window", validFrom: D(2025, 1, 1), validUntil: D(2026, 1, 1),
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        await SeedAsync("not-yet-true", validFrom: D(2025, 9, 1), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        await SeedAsync("no-longer-believed", validFrom: D(2025, 1, 1), validUntil: D(2026, 1, 1),
            createdAt: D(2025, 1, 1), invalidatedAt: D(2025, 4, 1));

        var asOf = D(2025, 6, 1);

        var facts = await _facts.SearchByCanonicalPredicatesAsOfAsync(
            [PredicateKey], limit: 50, scope: MemoryScope.For(Owner), asOf: asOf, systemAsOf: asOf);

        var ids = facts.Select(f => f.FactId).ToArray();
        ids.Should().Contain("in-window",
            "it was true at 2025-06-01 and still believed then");
        ids.Should().NotContain("not-yet-true",
            "valid_from 2025-09-01 is after the as-of instant — the VALID clock must exclude it");
        ids.Should().NotContain("no-longer-believed",
            "invalidated_at 2025-04-01 precedes the as-of instant — the TRANSACTION clock must exclude it");
    }

    /// <summary>
    /// The regression that motivated a separate method: the unbounded overload must still return
    /// everything, so the difference is demonstrably the clocks and not a narrower query.
    /// </summary>
    /// <remarks>
    /// Without this, a bug that made the as-of query match nothing at all would pass the test above
    /// for the wrong reason — excluding the two facts by being broken rather than by being correct.
    /// </remarks>
    [Fact]
    public async Task TheUnboundedExpansionStillSeesWhatTheAsOfOneFilters()
    {
        await SeedAsync("in-window", validFrom: D(2025, 1, 1), validUntil: D(2026, 1, 1),
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        await SeedAsync("not-yet-true", validFrom: D(2025, 9, 1), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: null);

        var unbounded = await _facts.SearchByCanonicalPredicatesAsync(
            [PredicateKey], limit: 50, scope: MemoryScope.For(Owner));

        unbounded.Select(f => f.FactId).Should().Contain(["in-window", "not-yet-true"],
            "the live query has no valid-time filter, so the as-of exclusions above are the clocks "
            + "doing work rather than the query matching nothing");
    }

    private static DateTimeOffset D(int y, int m, int d) => new(y, m, d, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Upserts through the repo (so predicate_key is canonicalised the way reads expect), then pins
    /// the clocks via raw Cypher so the boundaries are exact rather than wall-clock dependent.
    /// </summary>
    private async Task SeedAsync(
        string id, DateTimeOffset validFrom, DateTimeOffset? validUntil,
        DateTimeOffset createdAt, DateTimeOffset? invalidatedAt)
    {
        await _facts.UpsertAsync(new Fact
        {
            FactId = id, Subject = id, Predicate = Predicate, Object = "roofline",
            Confidence = 0.9, Embedding = Emb, OwnerId = Owner,
            CreatedAtUtc = createdAt, ValidFrom = validFrom, ValidUntil = validUntil,
        });

        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            @"MATCH (f:Fact {id: $id})
              SET f.created_at     = datetime($createdAt),
                  f.valid_from     = datetime($validFrom),
                  f.valid_until    = CASE WHEN $validUntil IS NULL THEN null ELSE datetime($validUntil) END,
                  f.invalidated_at = CASE WHEN $invalidatedAt IS NULL THEN null ELSE datetime($invalidatedAt) END",
            new
            {
                id,
                createdAt = createdAt.UtcDateTime.ToString("O"),
                validFrom = validFrom.UtcDateTime.ToString("O"),
                validUntil = validUntil?.UtcDateTime.ToString("O"),
                invalidatedAt = invalidatedAt?.UtcDateTime.ToString("O"),
            });
    }
}
