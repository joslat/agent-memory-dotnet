// SPDX-License-Identifier: Apache-2.0
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
    private readonly PackMeaning? _meaning;
    private readonly bool _maintainLocalCopies;

    /// <summary>Binds the store to the host's Neo4j database; with <paramref name="meaning"/>, imported records are embedded too.</summary>
    /// <remarks>With maintainLocalCopies false (default), no local-copy queries or locks are issued.</remarks>
    public Neo4jKnowledgePackStore(INeo4jTransactionRunner runner, TimeProvider? clock = null, PackMeaning? meaning = null, bool maintainLocalCopies = false)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _clock = clock ?? TimeProvider.System;
        _meaning = meaning;
        _maintainLocalCopies = maintainLocalCopies;
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
        Task<T> Step<T>(string step, Func<Task<T>> work) =>
            PackOperationTrace.StepAsync(PackOperations.Import, step, pack.Id, pack.Version, pack.Digest, work, cancellationToken, RefusalKind);
        await Step(PackSteps.Schema, () => ApplySchemaAsync(resolution, pack.Id, cancellationToken)).ConfigureAwait(false);
        var result = await Step(PackSteps.Records, () => new Neo4jModuleStore(_runner, resolution).ImportPackAsync(pack, atUtc.ToUniversalTime(), cancellationToken))
            .ConfigureAwait(false);
        // After the records' transaction (feature 19): an embedder that fails leaves the pack imported and found by words; the
        // next import embeds what is still missing.
        if (_meaning is not null)
            await Step(PackSteps.MeaningIndex, async () => { await _meaning.IndexAsync(_runner, resolution, cancellationToken).ConfigureAwait(false); return true; })
                .ConfigureAwait(false);
        return result;
    }

    /// <inheritdoc/>
    public async Task<KnowledgePackEraseResult> EraseAsync(ModuleManifest pack, IReadOnlyList<ModuleErasure> erasures, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(erasures);
        var resolution = KnowledgePack.ResolutionOf(pack);
        Task<T> Step<T>(string step, Func<Task<T>> work) =>
            PackOperationTrace.StepAsync(PackOperations.Erase, step, pack.Id, pack.Version.Value, null, work, cancellationToken, RefusalKind);
        await Step(PackSteps.Schema, () => ApplySchemaAsync(resolution, pack.Id, cancellationToken)).ConfigureAwait(false);
        var erasureRunner = _maintainLocalCopies ? new PackLocalErasureRunner(_runner, pack, erasures) : _runner;
        var erased = await Step(PackSteps.Erasures, () => new Neo4jModuleStore(erasureRunner, resolution).ErasePackAsync(pack.Id, erasures, atUtc.ToUniversalTime(), cancellationToken))
            .ConfigureAwait(false);
        // An erased record's vector is made from its text: it goes too (feature 19, R-CORE-133).
        await Step(PackSteps.MeaningForget, async () => { await PackMeaning.ForgetOrphansAsync(_runner, cancellationToken).ConfigureAwait(false); return true; })
            .ConfigureAwait(false);
        return erased;
    }

    /// <inheritdoc/>
    public async Task<KnowledgePackImportResult> RemoveAsync(ModuleManifest pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var resolution = KnowledgePack.ResolutionOf(pack);
        Task<T> Step<T>(string step, Func<Task<T>> work) =>
            PackOperationTrace.StepAsync(PackOperations.Remove, step, pack.Id, pack.Version.Value, null, work, cancellationToken, RefusalKind);
        var plan = await Step(PackSteps.State, () => new ModuleMigrator(_runner, resolution, _clock).PlanAsync(cancellationToken)).ConfigureAwait(false);
        if (plan.Applied.Count == 0) return new KnowledgePackImportResult { PackId = pack.Id };
        var removed = await Step(PackSteps.Records, () => new Neo4jModuleStore(_runner, resolution).RemovePackAsync(pack.Id, atUtc.ToUniversalTime(), cancellationToken))
            .ConfigureAwait(false);
        // Closed records keep no vector (feature 19).
        await Step(PackSteps.MeaningForget, async () => { await PackMeaning.ForgetClosedAsync(_runner, cancellationToken).ConfigureAwait(false); return true; })
            .ConfigureAwait(false);
        return removed;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the labels the id owns in the SDK namespace ledger and the live records it wrote under them, and closes them in one
    /// transaction with the checks a removal by manifest makes. Pending or unfinished schema steps of the id are refused, as a
    /// removal by manifest refuses them. An id that owns no name here closes nothing and writes nothing.
    /// </remarks>
    public async Task<KnowledgePackImportResult> RemoveByIdAsync(string packId, DateTimeOffset atUtc, CancellationToken cancellationToken = default)
    {
        if (!ModuleIdentity.IsValidModuleId(packId)) throw new ArgumentException("Expected a pack id.", nameof(packId));
        Task<T> Step<T>(string step, Func<Task<T>> work) =>
            PackOperationTrace.StepAsync(PackOperations.Remove, step, packId, null, null, work, cancellationToken, RefusalKind);
        var closed = await Step(PackSteps.Records, () => PackOwnership.RemoveByIdAsync(_runner, packId, atUtc.ToUniversalTime(), cancellationToken))
            .ConfigureAwait(false);
        if (closed is null) return new KnowledgePackImportResult { PackId = packId };
        // Closed records keep no vector (feature 19), however they were closed.
        await Step(PackSteps.MeaningForget, async () => { await PackMeaning.ForgetClosedAsync(_runner, cancellationToken).ConfigureAwait(false); return true; })
            .ConfigureAwait(false);
        return closed;
    }

    /// <summary>Applies the pack's derived schema; a problem the migrator finds is a refusal, not a database failure.</summary>
    private async Task<bool> ApplySchemaAsync(ModuleResolution resolution, string packId, CancellationToken cancellationToken)
    {
        var migration = await new ModuleMigrator(_runner, resolution, _clock).ApplyAsync("knowledge-pack:" + packId, cancellationToken).ConfigureAwait(false);
        if (migration.Problems.Count != 0) throw new KnowledgePackException(migration.Problems);
        return true;
    }

    /// <summary>A record the module store refuses, or a schema the migrator refuses, is the pack's problem, not the database's.</summary>
    private static string? RefusalKind(Exception error) =>
        error is ModuleRecordRefusal || error.Data.Contains(ModuleMigrator.RefusalKey) ? PackFailureKinds.Refused : null;
}
