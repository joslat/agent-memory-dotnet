using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>
/// Host-generated module snapshots in the host's Neo4j database (coding packs CMP-2a): the module's schema first, then the
/// records in batches, writing only what changed and closing what disappeared.
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
        var resolution = KnowledgePack.ResolutionOf(snapshot.Module);
        var migration = await new ModuleMigrator(runner, resolution, clock).ApplyAsync("module-snapshot:" + snapshot.Module.Id, cancellationToken).ConfigureAwait(false);
        if (migration.Problems.Count != 0) throw new KnowledgePackException(migration.Problems);
        return await new Neo4jModuleStore(runner, resolution).ImportSnapshotAsync(snapshot, atUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
    }
}
