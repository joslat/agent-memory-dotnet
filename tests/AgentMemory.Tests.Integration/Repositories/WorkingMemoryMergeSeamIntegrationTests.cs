using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 30.4b — merging entities recompiles the owner's working-memory block, through the PRODUCTION
/// merge path.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is an integration test rather than a unit test.</b> The design named
/// <c>MergeEntitiesAsync</c> as a rebuild seam and it was never built. The obvious fix — decorate
/// <see cref="AgentMemory.Abstractions.Repositories.IEntityRepository"/> — was built, passed 14 unit
/// tests and the full integration suite, and was WRONG: <see cref="Neo4jEntityRepository"/> also
/// implements <c>IUpsertPersistsProvenance</c>, <c>IBatchMemoryRepository&lt;Entity&gt;</c> and
/// <c>IFusedBatchMemoryRepository&lt;Entity&gt;</c>, and a wrapper implementing only the one
/// interface silently strips all three. That collapsed the batch write paths into per-item queries
/// and re-added provenance writes the marker exists to skip — <b>8 → 115 Cypher queries</b> on the
/// 50-message extraction scenario, invisible to every functional test and caught only by the
/// hermetic counter gate.
/// </para>
/// <para>
/// So the seam lives inside the repository, and the guard that it is wired has to drive the real
/// <c>MergeEntitiesAsync</c> against a real database. A unit test with a substituted repository
/// would pass against either design, including the broken one.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class WorkingMemoryMergeSeamIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jWorkingMemoryService _workingMemory;

    private static readonly MemoryScope Alice = MemoryScope.For("alice", includeShared: false);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    private sealed class Ids : IIdGenerator
    {
        public string GenerateId() => Guid.NewGuid().ToString("N");
    }

    public WorkingMemoryMergeSeamIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;

        var options = new MemoryOptions();
        options.WorkingMemory.Enabled = true;
        options.WorkingMemory.MinFactMentionCount = 1;

        _workingMemory = new Neo4jWorkingMemoryService(
            fixture.TransactionRunner, new FixedClock(), new Ids(),
            Options.Create(options), NullLogger<Neo4jWorkingMemoryService>.Instance);

        _entities = new Neo4jEntityRepository(
            fixture.TransactionRunner,
            NullLogger<Neo4jEntityRepository>.Instance,
            memoryOptions: Options.Create(options),
            workingMemory: _workingMemory);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Entity NewEntity(string name) => new()
    {
        EntityId = Guid.NewGuid().ToString("N"),
        Name = name,
        Type = "Organization",
        Confidence = 0.95,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        OwnerId = "alice",
    };

    [Fact]
    public async Task AfterAMergeTheBlockIsRecompiledWithoutAHandCalledRebuild()
    {
        var duplicate = await _entities.UpsertAsync(NewEntity("Acme Corporation"));
        var canonical = await _entities.UpsertAsync(NewEntity("Acme Corp"));
        await _workingMemory.RebuildAsync("alice");

        var before = await _workingMemory.GetAsync("alice");
        before!.Text.Should().Contain("Acme Corporation", "the duplicate is in the block to start with");

        // THE seam. The production merge call, with no rebuild afterwards — the contract is that
        // after the call returns the block is already current.
        var merged = await _entities.MergeEntitiesAsync(
            duplicate.EntityId, canonical.EntityId, Alice);

        merged.Should().BeTrue("the fixture merges two of the owner's own entities");

        // Asserted on CONTENT, not on BuiltAtUtc: the tier hash-short-circuits an unchanged
        // recompile, so a timestamp can be unchanged even when the seam fired correctly. The
        // question that matters is whether the block still names an entity that no longer exists.
        var after = await _workingMemory.GetAsync("alice");
        after!.Text.Should().NotContain(
            "Acme Corporation",
            "the merge folded that entity into the canonical one, so a block still naming it is "
            + "stale — if this fails the rebuild hook is not wired to the merge path");
    }

    /// <summary>
    /// A guarded / cross-owner / non-existent merge is a true no-op, so it must not recompile
    /// anything either — rebuilding there would spend work on exactly the calls the isolation guard
    /// exists to make free.
    /// </summary>
    [Fact]
    public async Task AGuardedNoOpMergeDoesNotRecompileTheBlock()
    {
        await _entities.UpsertAsync(NewEntity("Acme Corp"));
        await _workingMemory.RebuildAsync("alice");
        var before = await _workingMemory.GetAsync("alice");
        before.Should().NotBeNull();

        var merged = await _entities.MergeEntitiesAsync(
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), Alice);

        merged.Should().BeFalse("neither entity exists");

        var after = await _workingMemory.GetAsync("alice");
        after!.ContentHash.Should().Be(
            before!.ContentHash, "a merge that matched nothing changed nothing");
    }
}
