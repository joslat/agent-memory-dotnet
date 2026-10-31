using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>
/// Knowledge packs in the host's Neo4j database (Extensibility 0.13 §K3). Each pack is resolved on its own: an import applies
/// its derived schema (constraints, indexes and the search views' full-text indexes), then writes its shared records.
/// </summary>
[Experimental("AMEXT001")]
public sealed class Neo4jKnowledgePackStore : IKnowledgePackStore
{
    private readonly INeo4jTransactionRunner _runner;
    private readonly TimeProvider _clock;

    /// <summary>Binds the store to the host's Neo4j database.</summary>
    public Neo4jKnowledgePackStore(INeo4jTransactionRunner runner, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc/>
    public async Task<KnowledgePackState> GetStateAsync(ModuleManifest pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var resolution = KnowledgePack.ResolutionOf(pack);
        var plan = await new ModuleMigrator(_runner, resolution, _clock).PlanAsync(cancellationToken).ConfigureAwait(false);
        // Never imported: no schema, so nothing to read.
        if (plan.Applied.Count == 0) return new KnowledgePackState { PackId = pack.Id };
        var store = new Neo4jModuleStore(_runner, resolution);
        var live = await _runner.ReadAsync(transaction => store.PackLiveAsync(transaction, resolution.Modules.Single(), cancellationToken), cancellationToken)
            .ConfigureAwait(false);
        var version = live.Values.Where(v => v is not null).Select(v => ModuleVersion.Parse(v!)).Max();
        return new KnowledgePackState { PackId = pack.Id, Version = version?.Value, LiveRecords = live.Count };
    }

    /// <inheritdoc/>
    public async Task<KnowledgePackImportResult> ImportAsync(KnowledgePack pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var resolution = pack.Resolution;
        var migration = await new ModuleMigrator(_runner, resolution, _clock).ApplyAsync("knowledge-pack:" + pack.Id, cancellationToken).ConfigureAwait(false);
        if (migration.Problems.Count != 0) throw new KnowledgePackException(migration.Problems);
        return await new Neo4jModuleStore(_runner, resolution).ImportPackAsync(pack, atUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<KnowledgePackEraseResult> EraseAsync(ModuleManifest pack, IReadOnlyList<ModuleErasure> erasures, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(erasures);
        var resolution = KnowledgePack.ResolutionOf(pack);
        var migration = await new ModuleMigrator(_runner, resolution, _clock).ApplyAsync("knowledge-pack:" + pack.Id, cancellationToken).ConfigureAwait(false);
        if (migration.Problems.Count != 0) throw new KnowledgePackException(migration.Problems);
        return await new Neo4jModuleStore(_runner, resolution).ErasePackAsync(pack.Id, erasures, atUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<KnowledgePackImportResult> RemoveAsync(ModuleManifest pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var resolution = KnowledgePack.ResolutionOf(pack);
        var plan = await new ModuleMigrator(_runner, resolution, _clock).PlanAsync(cancellationToken).ConfigureAwait(false);
        if (plan.Applied.Count == 0) return new KnowledgePackImportResult { PackId = pack.Id };
        return await new Neo4jModuleStore(_runner, resolution).RemovePackAsync(pack.Id, atUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false);
    }
}
