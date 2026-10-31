using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>
/// Host-generated module snapshots in the host's Neo4j database (coding packs CMP-2a): the module's schema first, then the
/// records in batches, writing only what changed and closing what disappeared. A snapshot may link into the records of the
/// modules it depends on (CODING-hierarchy); a link whose target is not in the store is left out and counted.
/// </summary>
[Experimental("AMEXT001")]
public sealed class Neo4jModuleSnapshotStore(INeo4jTransactionRunner runner, TimeProvider? clock = null) : IModuleSnapshotStore
{
    /// <inheritdoc/>
    public async Task<ModuleSnapshotResult> ImportAsync(ModuleSnapshot snapshot, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var problems = snapshot.Problems();
        if (problems.Count != 0) throw new KnowledgePackException(problems);
        // With its dependencies (CODING-hierarchy), so its links into their records resolve; their schema is applied too, if pending.
        var resolution = snapshot.Resolve();
        var migration = await new ModuleMigrator(runner, resolution, clock).ApplyAsync("module-snapshot:" + snapshot.Module.Id, cancellationToken).ConfigureAwait(false);
        if (migration.Problems.Count != 0) throw new KnowledgePackException(migration.Problems);
        return await new Neo4jModuleStore(runner, resolution).ImportSnapshotAsync(snapshot, atUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Imports a snapshot pack's snapshot after applying the erasures that name the pack that carried it (feature 08, R-CORE-133):
    /// every version of each erased record it carried is removed, a tombstone is kept, and no later snapshot it carries writes it.
    /// </summary>
    public async Task<ModuleSnapshotResult> ImportAsync(ModuleSnapshot snapshot, IReadOnlyList<ModuleErasure> erasures, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(erasures);
        if (snapshot.Carrier is { } carrier && erasures.Where(e => e.PackId == carrier.Id).ToList() is { Count: > 0 } mine)
        {
            var resolution = snapshot.Resolve();
            var migration = await new ModuleMigrator(runner, resolution, clock).ApplyAsync("module-snapshot:" + snapshot.Module.Id, cancellationToken).ConfigureAwait(false);
            if (migration.Problems.Count != 0) throw new KnowledgePackException(migration.Problems);
            await new Neo4jModuleStore(runner, resolution).ErasePackAsync(carrier.Id, mine, atUtc.ToUniversalTime(), cancellationToken, snapshot.Module.Id).ConfigureAwait(false);
        }
        return await ImportAsync(snapshot, atUtc, cancellationToken).ConfigureAwait(false);
    }
}
