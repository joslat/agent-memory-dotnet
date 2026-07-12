using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// Live-DB coverage for <see cref="Neo4jMemoryDecayService"/>: the server-side decay/prune Cypher.
/// Verifies that stale low-score nodes are pruned while fresh ones survive, that an owner scope confines
/// the prune to that owner's own nodes (never another owner's, never shared), that access-timestamp bumps
/// persist, and that the retention score reflects the decay formula.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class Neo4jMemoryDecayServiceIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jMemoryDecayService _service;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    public Neo4jMemoryDecayServiceIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(_now);
        _service = new Neo4jMemoryDecayService(
            _fixture.TransactionRunner,
            clock,
            Options.Create(new MemoryDecayOptions()), // defaults: halfLife=30d, minScore=0.1, boost=0.2
            NullLogger<Neo4jMemoryDecayService>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task PruneExpiredMemoriesAsync_SoftInvalidatesStale_AndKeepsFreshActive()
    {
        var staleEntity = await SeedAsync("Entity", confidence: 0.5, daysOld: 400);
        var staleFact = await SeedAsync("Fact", confidence: 0.4, daysOld: 400);
        var stalePref = await SeedAsync("Preference", confidence: 0.5, daysOld: 400);
        var freshEntity = await SeedAsync("Entity", confidence: 0.9, daysOld: 0);
        var freshFact = await SeedAsync("Fact", confidence: 0.95, daysOld: 1);

        var pruned = await _service.PruneExpiredMemoriesAsync();

        pruned.Should().Be(3);
        // Non-destructive by default (D4): stale nodes are kept but invalidated; fresh ones stay active.
        (await ExistsAsync(staleEntity)).Should().BeTrue("non-destructive decay keeps the node, not deletes it");
        (await IsInvalidatedAsync(staleEntity)).Should().BeTrue();
        (await IsInvalidatedAsync(staleFact)).Should().BeTrue();
        (await IsInvalidatedAsync(stalePref)).Should().BeTrue();
        (await IsInvalidatedAsync(freshEntity)).Should().BeFalse("a fresh node is not invalidated");
        (await IsInvalidatedAsync(freshFact)).Should().BeFalse();
    }

    [Fact]
    public async Task PruneExpiredMemoriesAsync_Idempotent_DoesNotReCountInvalidated()
    {
        await SeedAsync("Fact", confidence: 0.3, daysOld: 400);

        (await _service.PruneExpiredMemoriesAsync()).Should().Be(1, "first run invalidates the stale fact");
        (await _service.PruneExpiredMemoriesAsync()).Should().Be(0, "a re-run must skip already-invalidated nodes");
    }

    [Fact]
    public async Task PruneExpiredMemoriesAsync_Scoped_OnlyInvalidatesOwnNodes()
    {
        var ownStale = await SeedAsync("Entity", confidence: 0.5, daysOld: 400, ownerId: "user-A");
        var otherStale = await SeedAsync("Entity", confidence: 0.5, daysOld: 400, ownerId: "user-B");
        var sharedStale = await SeedAsync("Entity", confidence: 0.5, daysOld: 400, ownerId: null);

        var pruned = await _service.PruneExpiredMemoriesAsync(MemoryScope.For("user-A"));

        pruned.Should().Be(1);
        (await IsInvalidatedAsync(ownStale)).Should().BeTrue("owner's own stale node is invalidated");
        (await IsInvalidatedAsync(otherStale)).Should().BeFalse("another owner's node must never be touched");
        (await IsInvalidatedAsync(sharedStale)).Should().BeFalse("shared/global nodes must never be touched in a scoped prune");
    }

    [Fact]
    public async Task PruneExpiredMemoriesAsync_Destructive_HardDeletes_WhenOptedIn()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(_now);
        var destructive = new Neo4jMemoryDecayService(
            _fixture.TransactionRunner, clock,
            Options.Create(new MemoryDecayOptions { NonDestructive = false }), // explicit hard purge
            NullLogger<Neo4jMemoryDecayService>.Instance);

        var stale = await SeedAsync("Fact", confidence: 0.3, daysOld: 400);

        var pruned = await destructive.PruneExpiredMemoriesAsync();

        pruned.Should().BeGreaterThan(0);
        (await ExistsAsync(stale)).Should().BeFalse("opt-in destructive prune hard-deletes for storage reclamation / GDPR");
    }

    [Fact]
    public async Task UpdateAccessTimestampAsync_IncrementsAccessCount_AndSetsTimestamp()
    {
        var id = await SeedAsync("Entity", confidence: 0.9, daysOld: 10, accessCount: 2, ownerId: "user-A");

        await _service.UpdateAccessTimestampAsync(id, MemoryNodeKind.Entity);

        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            @"MATCH (n:Entity {id:$id})
              OPTIONAL MATCH (audit:MemoryReadAudit {memory_id:$id})
              RETURN n.access_count AS ac,
                     n.last_accessed_at IS NOT NULL AS hasTs,
                     count(audit) AS auditCount,
                     collect(audit.kind) AS auditKinds,
                     collect(audit.owner_id) AS auditOwners,
                     all(readAt IN collect(audit.read_at) WHERE readAt IS NOT NULL) AS auditRowsHaveReadAt",
            new { id });
        var record = await cursor.SingleAsync();
        global::Neo4j.Driver.ValueExtensions.As<long>(record["ac"]).Should().Be(3);
        global::Neo4j.Driver.ValueExtensions.As<bool>(record["hasTs"]).Should().BeTrue();
        global::Neo4j.Driver.ValueExtensions.As<long>(record["auditCount"]).Should().Be(1);
        global::Neo4j.Driver.ValueExtensions.As<IList<object>>(record["auditKinds"]).Should().Contain("Entity");
        global::Neo4j.Driver.ValueExtensions.As<IList<object>>(record["auditOwners"]).Should().Contain("user-A");
        global::Neo4j.Driver.ValueExtensions.As<bool>(record["auditRowsHaveReadAt"]).Should().BeTrue();
    }

    [Fact]
    public async Task CalculateRetentionScoreAsync_ReflectsDecayFormula()
    {
        var freshId = await SeedAsync("Entity", confidence: 1.0, daysOld: 0, accessCount: 0);
        var halfLifeId = await SeedAsync("Fact", confidence: 1.0, daysOld: 30, accessCount: 0);

        (await _service.CalculateRetentionScoreAsync(freshId, MemoryNodeKind.Entity)).Should().BeApproximately(1.0, 0.05);
        (await _service.CalculateRetentionScoreAsync(halfLifeId, MemoryNodeKind.Fact)).Should().BeApproximately(0.5, 0.05);
    }

    [Fact]
    public async Task CalculateRetentionScoreAsync_MissingNode_ReturnsZero()
    {
        (await _service.CalculateRetentionScoreAsync($"missing-{Guid.NewGuid():N}", MemoryNodeKind.Entity)).Should().Be(0.0);
    }

    // (Former UnsupportedLabel_Throws theory removed: MemoryNodeKind is a closed enum, so an unsupported /
    // injection label can no longer be passed — it's a compile error, and the label is never caller-supplied.)

    [Fact]
    public async Task CalculateRetentionScoreAsync_NullCreatedAt_ReturnsZero()
    {
        // decay-2: a node with no created_at is never prune-eligible; the score must agree (0), not
        // default to "now" and look artificially fresh.
        var id = $"entity-{Guid.NewGuid():N}";
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            "CREATE (n:Entity {id: $id, confidence: 0.9, access_count: 0})", // no created_at
            new { id });

        (await _service.CalculateRetentionScoreAsync(id, MemoryNodeKind.Entity)).Should().Be(0.0);
    }

    [Fact]
    public async Task PruneExpiredMemoriesAsync_PrunesRepositoryWrittenEntity()
    {
        // test-1: prove the prune works against repository-written property names (not just the
        // raw-Cypher-seeded read names) — seed via UpsertAsync, then back-date created_at, then prune.
        var repo = new Neo4jEntityRepository(_fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        var id = $"entity-{Guid.NewGuid():N}";
        await repo.UpsertAsync(new Entity
        {
            EntityId = id, Name = "Stale", Type = "Concept", Confidence = 0.3, CreatedAtUtc = DateTimeOffset.UtcNow
        });
        // Back-date created_at far enough that the decay score falls below minScore (0.1).
        await using (var session = _fixture.Driver.AsyncSession())
        {
            await session.RunAsync(
                "MATCH (e:Entity {id:$id}) SET e.created_at = datetime() - duration({days: 400})",
                new { id });
        }

        var pruned = await _service.PruneExpiredMemoriesAsync();

        pruned.Should().BeGreaterThan(0);
        (await IsInvalidatedAsync(id)).Should().BeTrue("a repository-written stale entity must be prunable (soft-invalidated)");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>Seeds one node of <paramref name="label"/> with a created_at <paramref name="daysOld"/> days
    /// in the past (relative to the DB clock). Returns the node id.</summary>
    private async Task<string> SeedAsync(
        string label, double confidence, int daysOld, int accessCount = 0, string? ownerId = null)
    {
        var id = $"{label.ToLowerInvariant()}-{Guid.NewGuid():N}";
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            $@"CREATE (n:{label} {{
                   id: $id,
                   owner_id: $ownerId,
                   confidence: $confidence,
                   access_count: $accessCount,
                   created_at: datetime() - duration({{days: $daysOld}})
               }})",
            new { id, ownerId = (object?)ownerId, confidence, accessCount, daysOld });
        return id;
    }

    private async Task<bool> ExistsAsync(string id)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync("MATCH (n {id:$id}) RETURN count(n) AS c", new { id });
        var record = await cursor.SingleAsync();
        return global::Neo4j.Driver.ValueExtensions.As<long>(record["c"]) > 0;
    }

    /// <summary>Whether a node exists AND has been soft-invalidated (invalidated_at stamped).</summary>
    private async Task<bool> IsInvalidatedAsync(string id)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (n {id:$id}) RETURN n.invalidated_at IS NOT NULL AS inv", new { id });
        var records = await cursor.ToListAsync();
        return records.Count > 0 && global::Neo4j.Driver.ValueExtensions.As<bool>(records[0]["inv"]);
    }
}
