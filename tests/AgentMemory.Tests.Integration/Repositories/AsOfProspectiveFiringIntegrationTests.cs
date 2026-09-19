using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// D2 against live Neo4j: prospective firing at a point in time must volunteer only what came due
/// <b>as of that instant</b>, and only what was believed then.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests for this feature assert at the assembler seam over substitutes, so they prove the
/// clocks are <i>passed</i>. They cannot prove the Cypher <i>uses</i> them, or that it parses at all.
/// This is the condition the authorization named: the harness refusal stays until the claim is TRUE,
/// and the claim is not true until the query has run against a real store.
/// </para>
/// <para>
/// <b>The failure this guards is the one that does not look like a failure.</b> A reminder fired from
/// the machine clock into a point-in-time recall is a well-formed reminder about the wrong world, and
/// a reminder recorded after the transaction instant is a well-formed reminder the system did not yet
/// know. Neither loses a row; both add one, and the extra row is indistinguishable from a reminder
/// that legitimately came due.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class AsOfProspectiveFiringIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;

    private static readonly float[] Emb = [0.5f, 0.5f, 0.5f, 0.5f];
    private const string Owner = "owner-d2";

    public AsOfProspectiveFiringIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _facts = new Neo4jFactRepository(fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>The invariant: only what came due in the window, and only what was believed then.</summary>
    [Fact]
    public async Task FiringAsOf_ReturnsOnlyWhatCameDueInTheWindowAndWasBelievedThen()
    {
        // valid_from inside (since, validAsOf] -- the one that must fire.
        await SeedAsync("came-due", validFrom: D(2025, 5, 20), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        // Opened BEFORE the window: due already, not NEWLY due. The `since` bound must exclude it.
        await SeedAsync("due-long-ago", validFrom: D(2025, 1, 15), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        // Opens AFTER the as-of instant: the VALID clock must exclude it.
        await SeedAsync("not-yet-due", validFrom: D(2025, 9, 1), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        // Came due in the window, but only RECORDED after the transaction instant: the TRANSACTION
        // clock must exclude it. This is the row a single-clock implementation returns.
        await SeedAsync("not-yet-known", validFrom: D(2025, 5, 22), validUntil: null,
            createdAt: D(2025, 8, 1), invalidatedAt: null);
        // Came due in the window and was retracted before it: also excluded by the transaction clock.
        await SeedAsync("retracted", validFrom: D(2025, 5, 21), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: D(2025, 5, 25));

        var validAsOf = D(2025, 6, 1);

        var result = await _facts.GetDueFactsAsOfAsync(
            since: D(2025, 5, 1),
            validAsOf: validAsOf,
            systemAsOf: validAsOf,
            expiringWindow: TimeSpan.FromDays(7),
            limit: 50,
            scope: MemoryScope.For(Owner));

        var due = result.Due.Select(f => f.FactId).ToArray();
        due.Should().Contain("came-due", "its validity opened inside (since, validAsOf]");
        due.Should().NotContain("due-long-ago", "it opened before the window — due, but not NEWLY due");
        due.Should().NotContain("not-yet-due", "valid_from is after the as-of instant");
        due.Should().NotContain("not-yet-known",
            "created_at 2025-08-01 is after the transaction instant — the system did not know it yet");
        due.Should().NotContain("retracted",
            "invalidated_at 2025-05-25 precedes the transaction instant — no longer believed");
    }

    /// <summary>
    /// THE CONTROL: the live overload still fires what the as-of one filters.
    /// </summary>
    /// <remarks>
    /// Without this, a query that matched nothing at all would pass the test above for entirely the
    /// wrong reason — excluding four facts by being broken rather than by being correct. The same trap
    /// the W1c integration test exists to avoid, and it caught a real one there.
    /// </remarks>
    [Fact]
    public async Task TheLiveFiringQueryStillSeesWhatTheAsOfOneFilters()
    {
        await SeedAsync("came-due", validFrom: D(2025, 5, 20), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        await SeedAsync("not-yet-known", validFrom: D(2025, 5, 22), validUntil: null,
            createdAt: D(2025, 8, 1), invalidatedAt: null);

        // The live overload has no transaction clock, so it judges both by valid_from alone.
        var live = await _facts.GetDueFactsAsync(
            since: D(2025, 5, 1),
            now: D(2025, 6, 1),
            expiringWindow: TimeSpan.FromDays(7),
            limit: 50,
            scope: MemoryScope.For(Owner));

        live.Due.Select(f => f.FactId).Should().Contain(["came-due", "not-yet-known"],
            "the live query filters on valid_from only, so the as-of exclusion of not-yet-known above "
            + "is the TRANSACTION clock doing work rather than the query matching nothing");
    }

    /// <summary>
    /// The expiring section hangs off the AS-OF instant, not the machine's.
    /// </summary>
    /// <remarks>
    /// A horizon measured from "now" answers a question nobody asked on a point-in-time read: what is
    /// about to expire TODAY, reported inside a reconstruction of last spring.
    /// </remarks>
    [Fact]
    public async Task TheExpiringHorizonIsMeasuredFromTheAsOfInstant()
    {
        // Closes 3 days after the as-of instant -- inside a 7-day horizon measured from THERE.
        await SeedAsync("expiring-soon", validFrom: D(2025, 1, 1), validUntil: D(2025, 6, 4),
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        // Closes well beyond that horizon.
        await SeedAsync("expiring-later", validFrom: D(2025, 1, 1), validUntil: D(2025, 12, 1),
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        // Already closed before the as-of instant: expired, not expiring. Reporting it would be a
        // tense error the reader acts on.
        await SeedAsync("already-expired", validFrom: D(2025, 1, 1), validUntil: D(2025, 3, 1),
            createdAt: D(2025, 1, 1), invalidatedAt: null);

        var validAsOf = D(2025, 6, 1);

        var result = await _facts.GetDueFactsAsOfAsync(
            since: D(2025, 5, 1),
            validAsOf: validAsOf,
            systemAsOf: validAsOf,
            expiringWindow: TimeSpan.FromDays(7),
            limit: 50,
            scope: MemoryScope.For(Owner));

        var expiring = result.Expiring.Select(f => f.FactId).ToArray();
        expiring.Should().Contain("expiring-soon");
        expiring.Should().NotContain("expiring-later", "it closes beyond the horizon from the as-of instant");
        expiring.Should().NotContain("already-expired", "it had closed before that instant");
    }

    /// <summary>Owner scoping holds on this path as it does on every other read here.</summary>
    [Fact]
    public async Task FiringIsScopedToTheOwner()
    {
        await SeedAsync("mine", validFrom: D(2025, 5, 20), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: null);
        await SeedAsync("theirs", validFrom: D(2025, 5, 20), validUntil: null,
            createdAt: D(2025, 1, 1), invalidatedAt: null, owner: "owner-other");

        var validAsOf = D(2025, 6, 1);

        var result = await _facts.GetDueFactsAsOfAsync(
            since: D(2025, 5, 1), validAsOf: validAsOf, systemAsOf: validAsOf,
            expiringWindow: TimeSpan.FromDays(7), limit: 50, scope: MemoryScope.For(Owner));

        result.Due.Select(f => f.FactId).Should().Contain("mine").And.NotContain("theirs");
    }

    private static DateTimeOffset D(int y, int m, int d) => new(y, m, d, 0, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Upserts through the repo, then pins the clocks via raw Cypher so the boundaries are exact
    /// rather than wall-clock dependent.
    /// </summary>
    private async Task SeedAsync(
        string id, DateTimeOffset validFrom, DateTimeOffset? validUntil,
        DateTimeOffset createdAt, DateTimeOffset? invalidatedAt, string owner = Owner)
    {
        await _facts.UpsertAsync(new Fact
        {
            FactId = id, Subject = id, Predicate = "is_due", Object = "true",
            Confidence = 0.9, Embedding = Emb, OwnerId = owner,
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
