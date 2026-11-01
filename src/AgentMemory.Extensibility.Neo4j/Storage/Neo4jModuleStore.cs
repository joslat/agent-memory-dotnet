using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Queries;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>Explicit module record writer using one Neo4j transaction per admitted provider batch.</summary>
/// <remarks>Never applies migrations. Source membership, current-version locking and link resolution
/// run inside the write transaction. A transactional invariant refusal rolls back the entire admitted
/// batch; unexpected failures propagate, since a lost acknowledgement cannot prove rollback.
/// Records and their provenance are retained after invalidation. Runtime registration is separate.</remarks>
[Experimental("AMEXT001")]
public sealed class Neo4jModuleStore : IModuleStore
{
    private readonly INeo4jTransactionRunner _runner;
    private readonly ModuleResolution _resolution;
    private readonly ModuleMigrator _migrator;
    private readonly ModuleRecordLinks _links;

    /// <summary>Binds one configured store to frozen metadata and optional explicit migration sources.</summary>
    public Neo4jModuleStore(INeo4jTransactionRunner runner, ModuleResolution resolution,
        IReadOnlyList<ModuleMigrationStep>? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.Succeeded) throw new ArgumentException("Successful metadata resolution is required.", nameof(resolution));
        _runner = runner; _resolution = resolution;
        _migrator = new(runner, resolution, scripts ?? [], null); _links = new(resolution);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProposalOutcome>> WriteAsync(ModuleWriteBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Proposals);
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.Proposals.Count == 0) return [];
        if (batch.Proposals.Any(p => p is null || p.Index < 0 || p.Proposal is null)
            || batch.Proposals.Select(p => p.Index).Distinct().Count() != batch.Proposals.Count)
            throw new ArgumentException("Batch requires distinct nonnegative proposal indices.", nameof(batch));
        ArgumentNullException.ThrowIfNull(batch.Request); ArgumentNullException.ThrowIfNull(batch.Provider);
        var request = batch.Request with
        {
            Messages = batch.Request.Messages?.Select(m => m is null ? null! : m with { }).ToList().AsReadOnly()!,
            ProviderIds = batch.Request.ProviderIds?.ToList().AsReadOnly()!
        };
        var provider = batch.Provider with { };
        var module = _resolution.Modules.SingleOrDefault(m => m.Id == batch.ModuleId);
        if (module is null || batch.ManifestFingerprint != _resolution.ManifestFingerprint || !ModuleIdentity.IsOwnedId(batch.ModuleId, provider.Id))
            return Refused(batch.Proposals, "Batch metadata/provider does not match the bound store.");
        var admitted = new List<AdmittedModuleProposal>(); var outcomes = new List<ProposalOutcome>();
        foreach (var input in batch.Proposals)
        {
            var admission = ModuleProposalAdmission.Admit(_resolution, provider, request, input.Proposal);
            if (admission.Accepted) admitted.Add(input with { Proposal = admission.Proposal! });
            else outcomes.Add(new() { Index = input.Index, Status = ProposalWriteStatus.Refused, Errors = admission.Errors });
        }
        if (admitted.Count == 0) return outcomes.AsReadOnly();
        try
        {
            var written = await _runner.WriteAsync(async transaction =>
            {
                var readiness = await _migrator.PlanInAsync(transaction, cancellationToken).ConfigureAwait(false);
                if (readiness.Problems.Count != 0 || readiness.Pending.Count != 0 || readiness.PendingNamespaces.Count != 0 || readiness.PendingBookkeeping.Count != 0)
                    throw new ModuleRecordRefusal("Module migrations are pending or inconsistent; apply them explicitly before writes.");
                await VerifySourcesAsync(transaction, request, admitted, cancellationToken).ConfigureAwait(false);
                var keys = admitted.Select(p => (p.Proposal, Type: module.Ontology!.Types.Single(t => t.Name == p.Proposal.Type)))
                    .Select(p => (Id: KeyId(module.Id, p.Type.Name, p.Type.Scope == "shared" ? null : request.Owner, p.Proposal.Key), p.Proposal, p.Type))
                    .GroupBy(p => p.Id).Select(g => g.First()).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
                foreach (var key in keys)
                    await LockAsync(transaction, module.Id, key.Type, key.Proposal.Key, key.Type.Scope == "shared" ? null : request.Owner, cancellationToken).ConfigureAwait(false);
                var results = new List<ProposalOutcome>();
                foreach (var proposal in admitted)
                    results.Add(await WriteOneAsync(transaction, module, request, proposal, batch.AtUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false));
                return results;
            }, cancellationToken).ConfigureAwait(false);
            outcomes.AddRange(written);
        }
        catch (ModuleRecordRefusal refusal) { outcomes.AddRange(Refused(admitted, refusal.Message)); }
        return outcomes.OrderBy(p => p.Index).ToList().AsReadOnly();
    }

    /// <summary>
    /// Writes a knowledge pack in one transaction (Extensibility 0.13 §K3): every key is locked in order, records are written
    /// targets first, and live records of the pack this version no longer holds are closed. A refusal rolls back all of it.
    /// </summary>
    internal async Task<KnowledgePackImportResult> ImportPackAsync(KnowledgePack pack, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        var module = _resolution.Modules.Single(m => m.Id == pack.Id);
        var request = PackRequest(pack.Id, atUtc);
        try
        {
            return await _runner.WriteAsync(async transaction =>
            {
                await ReadyAsync(transaction, cancellationToken).ConfigureAwait(false);
                // A record a tombstone names is never written again (feature 08, R-CORE-134), from any version or copy.
                var tombstones = await TombstonesAsync(transaction, module.Id, cancellationToken).ConfigureAwait(false);
                var proposals = WithoutErased(module, pack.Proposals, tombstones);
                var live = await PackLiveAsync(transaction, module, cancellationToken).ConfigureAwait(false);
                var seeded = proposals.Select(p => (p.Type, p.Key)).ToHashSet();
                var gone = live.Keys.Where(k => !seeded.Contains(k)).Order().ToArray();
                foreach (var (type, key) in seeded.Concat(gone).Select(k => (Type: module.Ontology!.Types.Single(t => t.Name == k.Type), k.Key))
                    .OrderBy(k => KeyId(module.Id, k.Type.Name, null, k.Key), StringComparer.Ordinal))
                    await LockAsync(transaction, module.Id, type, key, null, cancellationToken).ConfigureAwait(false);
                int created = 0, changed = 0, unchanged = 0, closed = 0;
                for (var index = 0; index < proposals.Count; index++)
                {
                    var proposal = proposals[index];
                    var outcome = await WriteOneAsync(transaction, module, request, new() { Index = index, Proposal = proposal }, atUtc, cancellationToken,
                        exact: true).ConfigureAwait(false);
                    if (outcome.Status == ProposalWriteStatus.NoOp) unchanged++;
                    else if (live.ContainsKey((proposal.Type, proposal.Key))) changed++;
                    else created++;
                }
                foreach (var key in gone)
                    if (await CloseAsync(transaction, module, request, key, atUtc, cancellationToken).ConfigureAwait(false)) closed++;
                return new KnowledgePackImportResult { PackId = pack.Id, Version = pack.Version, Digest = pack.Digest,
                    Created = created, Changed = changed, Unchanged = unchanged, Closed = closed };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ModuleRecordRefusal refusal) { throw new KnowledgePackException([refusal.Message]); }
    }

    // The records no tombstone names, each without its links into records one names (review F08, H5: a surviving record's link
    // into an erased one made an exact import fail for good; the link goes with the record, as memory drops it).
    private static List<ModuleProposal> WithoutErased(ModuleManifest module, IReadOnlyList<ModuleProposal> records,
        List<(string Type, string Salt, string KeyHash)> tombstones)
    {
        if (tombstones.Count == 0) return [.. records];
        bool Buried(string type, string key) => tombstones.Any(t => t.Type == type && t.KeyHash == ModuleErasure.KeyHash(t.Salt, key));
        return [.. records.Where(p => !Buried(p.Type, p.Key)).Select(p => p with
        {
            Links = [.. p.Links.Where(l => module.Ontology!.Relations.FirstOrDefault(r => r.Name == l.Relation && r.From == p.Type) is not { } relation || !Buried(relation.To, l.Target))],
        })];
    }

    // The tombstones of a pack's erased records (feature 08): type, salt and key hash; never a key.
    private static async Task<List<(string Type, string Salt, string KeyHash)>> TombstonesAsync(IAsyncQueryRunner transaction, string packId, CancellationToken cancellationToken)
    {
        var cursor = await transaction.RunAsync("MATCH (t:ModuleTombstone {pack:$pack}) RETURN t.type AS type, t.salt AS salt, t.key_hash AS hash",
            new Dictionary<string, object?> { ["pack"] = packId }).ConfigureAwait(false);
        var rows = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. rows.Select(r => (r["type"].As<string>(), r["salt"].As<string>(), r["hash"].As<string>()))];
    }

    /// <summary>
    /// Applies erasures to a pack's records in one transaction (feature 08, R-CORE-133): every version of each erased record and
    /// its key are removed with their links, and a tombstone of the pack, the type, the salt and the key's hash is kept.
    /// </summary>
    internal async Task<KnowledgePackEraseResult> ErasePackAsync(string packId, IReadOnlyList<ModuleErasure> erasures, DateTimeOffset atUtc, CancellationToken cancellationToken,
        string? moduleId = null)
    {
        // A snapshot pack's records live in its definition's module, carried by it (review F08, H3).
        var module = _resolution.Modules.Single(m => m.Id == (moduleId ?? packId));
        return await _runner.WriteAsync(async transaction =>
        {
            await ReadyAsync(transaction, cancellationToken).ConfigureAwait(false);
            var erased = 0;
            var tombstones = 0;
            foreach (var type in module.Ontology!.Types)
            {
                var named = erasures.SelectMany(e => e.Records.Where(r => r.Type == type.Name).Select(r => (Erasure: e, Record: r))).ToList();
                if (named.Count == 0) continue;
                var cursor = await transaction.RunAsync("MATCH (n:`" + type.Name + "`) WHERE coalesce(n.source_carrier, n.source_pack)=$pack RETURN DISTINCT n.key AS key",
                    new Dictionary<string, object?> { ["pack"] = packId }).ConfigureAwait(false);
                var keys = (await cursor.ToListAsync(cancellationToken).ConfigureAwait(false)).Select(r => r["key"].As<string>()).ToList();
                var gone = keys.Where(k => named.Any(n => n.Erasure.Erases(type.Name, k))).ToList();
                if (gone.Count > 0)
                {
                    await transaction.RunAsync("MATCH (n:`" + type.Name + "`) WHERE coalesce(n.source_carrier, n.source_pack)=$pack AND n.key IN $keys "
                        + "OPTIONAL MATCH (k:ModuleKey {id:n.key_id}) DETACH DELETE n, k",
                        new Dictionary<string, object?> { ["pack"] = packId, ["keys"] = gone }).ConfigureAwait(false);
                    erased += gone.Count;
                }
                // A tombstone for every record named, held or not: a later copy is blocked as well.
                foreach (var (erasure, record) in named)
                {
                    await transaction.RunAsync("MERGE (t:ModuleTombstone {pack:$pack, type:$type, key_hash:$hash}) "
                        + "ON CREATE SET t.salt=$salt, t.erasure=$erasure, t.reason=$reason, t.erased_at=datetime($at)",
                        new Dictionary<string, object?>
                        {
                            ["pack"] = packId, ["type"] = type.Name, ["hash"] = record.KeyHash ?? erasure.HashOf(record.Key!), ["salt"] = erasure.Salt,
                            ["erasure"] = erasure.Statement, ["reason"] = erasure.Reason, ["at"] = atUtc.ToString("O", CultureInfo.InvariantCulture),
                        }).ConfigureAwait(false);
                    tombstones++;
                }
            }
            return new KnowledgePackEraseResult { PackId = packId, Erased = erased, Tombstones = tombstones };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records one snapshot transaction writes; a first code map is far larger than one transaction should hold.</summary>
    internal const int SnapshotBatch = 400;

    /// <summary>
    /// A host-generated snapshot (CMP-2a), in batches of <see cref="SnapshotBatch"/>, targets first: what changed is written, what
    /// is equal stays as it was (links included), and what the snapshot no longer holds is closed, history kept. A failed batch
    /// leaves the earlier ones written; importing the same snapshot again completes it.
    /// </summary>
    // Links into dependencies' records left out by the snapshot import running on this store (CODING-hierarchy).
    private int _linksLeftOut;

    internal async Task<ModuleSnapshotResult> ImportSnapshotAsync(ModuleSnapshot snapshot, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _linksLeftOut, 0);
        var module = _resolution.Modules.Single(m => m.Id == snapshot.Module.Id);
        var request = PackRequest(module.Id, atUtc);
        var ordered = snapshot.ImportOrder(selfCycles: true);
        // What a snapshot pack's erasures buried never comes back with a later snapshot it carries (review F08, H3).
        var tombstones = snapshot.Carrier is { } carrier
            ? await _runner.ReadAsync(transaction => TombstonesAsync(transaction, carrier.Id, cancellationToken), cancellationToken).ConfigureAwait(false)
            : [];
        IReadOnlyList<ModuleProposal> records = WithoutErased(module, ordered, tombstones);
        var selfRelations = module.Ontology!.Relations.Where(r => r.From == r.To).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        try
        {
            var (live, state) = await _runner.ReadAsync(async transaction =>
            {
                await ReadyAsync(transaction, cancellationToken).ConfigureAwait(false);
                await DependenciesHeldAsync(transaction, module, records, cancellationToken).ConfigureAwait(false);
                return (await PackLiveAsync(transaction, module, cancellationToken).ConfigureAwait(false),
                    await SnapshotStateAsync(transaction, module, cancellationToken).ConfigureAwait(false));
            }, cancellationToken).ConfigureAwait(false);
            // A commit changes a few records of thousands: those whose properties and link targets are already stored are left
            // without a write (the per-record path would reach the same no-op, a dozen queries later).
            var pending = records.Where(r => !Same(module, r, state)).ToList();
            int created = 0, changed = 0, unchanged = records.Count - pending.Count, closed = 0;
            // A self link to a record this import has not written yet (types that use each other link in cycles) is written
            // once that record exists: added to the version this import wrote, or, for a record that was otherwise unchanged,
            // by writing it again whole, a new version, since gaining a link is a change.
            var written = live.Keys.ToHashSet();
            var deferred = new List<(ModuleProposal Proposal, IReadOnlyList<ProposedLink> Links, bool Stored)>();
            foreach (var batch in pending.Chunk(SnapshotBatch))
                await _runner.WriteAsync(async transaction =>
                {
                    for (var index = 0; index < batch.Length; index++)
                    {
                        var proposal = batch[index];
                        var forward = proposal.Links.Where(l => selfRelations.Contains(l.Relation) && !written.Contains((proposal.Type, l.Target))).ToList();
                        var now = forward.Count == 0 ? proposal : proposal with { Links = proposal.Links.Except(forward).ToList().AsReadOnly() };
                        var outcome = await WriteOneAsync(transaction, module, request, new() { Index = index, Proposal = now }, atUtc, cancellationToken,
                            exact: true).ConfigureAwait(false);
                        written.Add((proposal.Type, proposal.Key));
                        if (forward.Count != 0) deferred.Add((proposal, forward, outcome.Status == ProposalWriteStatus.Stored));
                        if (outcome.Status == ProposalWriteStatus.NoOp) unchanged++;
                        else if (live.ContainsKey((proposal.Type, proposal.Key))) changed++;
                        else created++;
                    }
                    return batch.Length;
                }, cancellationToken).ConfigureAwait(false);
            await LinkDeferredAsync(module, deferred.Where(d => d.Stored).SelectMany(d => d.Links.Select(l => (d.Proposal.Type, d.Proposal.Key, l.Relation, l.Target))),
                cancellationToken).ConfigureAwait(false);
            foreach (var batch in deferred.Where(d => !d.Stored).Select(d => d.Proposal).Chunk(SnapshotBatch))
                await _runner.WriteAsync(async transaction =>
                {
                    for (var index = 0; index < batch.Length; index++)
                        if ((await WriteOneAsync(transaction, module, request, new() { Index = index, Proposal = batch[index] }, atUtc, cancellationToken,
                                exact: true).ConfigureAwait(false)).Status == ProposalWriteStatus.Stored) { unchanged--; changed++; }
                    return batch.Length;
                }, cancellationToken).ConfigureAwait(false);
            var held = records.Select(r => (r.Type, r.Key)).ToHashSet();
            foreach (var batch in live.Keys.Where(k => !held.Contains(k)).OrderBy(k => KeyId(module.Id, k.Type, null, k.Key), StringComparer.Ordinal).Chunk(SnapshotBatch))
                closed += await _runner.WriteAsync(async transaction =>
                {
                    var count = 0;
                    foreach (var key in batch)
                        if (await CloseAsync(transaction, module, request, key, atUtc, cancellationToken).ConfigureAwait(false)) count++;
                    return count;
                }, cancellationToken).ConfigureAwait(false);
            return new ModuleSnapshotResult
            {
                ModuleId = module.Id, Source = snapshot.Source, Created = created, Changed = changed, Unchanged = unchanged, Closed = closed,
                LinksLeftOut = Volatile.Read(ref _linksLeftOut),
            };
        }
        catch (ModuleRecordRefusal refusal) { throw new KnowledgePackException([refusal.Message]); }
    }

    // Self links between live records by key identity, in batches per relation; every one must land.
    private async Task LinkDeferredAsync(ModuleManifest module, IEnumerable<(string Type, string Key, string Relation, string Target)> links,
        CancellationToken cancellationToken)
    {
        foreach (var group in links.GroupBy(l => (l.Type, l.Relation)))
            foreach (var batch in group.Chunk(SnapshotBatch))
            {
                var linked = await _runner.WriteAsync(async transaction =>
                {
                    var rows = await RowsAsync(transaction, ModuleRecordQueries.LinkLive(group.Key.Type, group.Key.Relation, group.Key.Type), new
                    {
                        links = batch.Select(l => new Dictionary<string, object>
                        {
                            ["source"] = KeyId(module.Id, l.Type, null, l.Key), ["target"] = KeyId(module.Id, l.Type, null, l.Target),
                        }).ToArray(),
                    }, cancellationToken).ConfigureAwait(false);
                    return rows.Single()["linked"].As<long>();
                }, cancellationToken).ConfigureAwait(false);
                if (linked != batch.Length)
                    throw new ModuleRecordRefusal($"{batch.Length - linked} {group.Key.Relation} links reach no live record after the import.");
            }
    }

    /// <summary>Every live record a snapshot wrote, by type and key: its stored properties and its links as (relation, target key).</summary>
    private static async Task<Dictionary<(string Type, string Key), (Dictionary<string, object> Row, HashSet<(string, string)> Links)>> SnapshotStateAsync(
        IAsyncQueryRunner runner, ModuleManifest module, CancellationToken cancellationToken)
    {
        var state = new Dictionary<(string, string), (Dictionary<string, object>, HashSet<(string, string)>)>();
        foreach (var type in module.Ontology!.Types)
        {
            var relations = module.Ontology.Relations.Where(r => r.From == type.Name).Select(r => r.Name).ToArray();
            foreach (var row in await RowsAsync(runner, ModuleRecordQueries.SnapshotState(type.Name), new { pack = module.Id, relations }, cancellationToken).ConfigureAwait(false))
            {
                var links = row["links"].As<List<object>>().OfType<List<object>>().Select(l => (l[0].As<string>(), l[1].As<string>())).ToHashSet();
                state[(type.Name, row["key"].As<string>())] = (row["row"].As<Dictionary<string, object>>(), links);
            }
        }
        return state;
    }

    // Equal to what is stored: every declared property (absent on both sides counts as equal) and the same links by target key.
    // Date and time properties go through the per-record path, which compares them as the store does.
    private static bool Same(ModuleManifest module, ModuleProposal proposal,
        Dictionary<(string Type, string Key), (Dictionary<string, object> Row, HashSet<(string, string)> Links)> state)
    {
        if (!state.TryGetValue((proposal.Type, proposal.Key), out var stored)) return false;
        // A life, given or stored, goes the per-record path, which compares it as the store does (feature 07 review, H1).
        if (proposal.Life is not null || ModuleRecordQueries.LifeProperties.Any(p => stored.Row.ContainsKey(p.Name))) return false;
        if (proposal.ConfidenceMicro is not null || stored.Row.ContainsKey("confidence_micro")) return false;
        var type = module.Ontology!.Types.Single(t => t.Name == proposal.Type);
        foreach (var (name, property) in type.Properties)
        {
            if (property.Type is "date" or "datetime") return false;
            var has = proposal.Properties.TryGetValue(name, out var value) && value.ValueKind != JsonValueKind.Null;
            var kept = stored.Row.TryGetValue(name, out var current) && current is not null;
            if (has != kept) return false;
            if (has && !Equals(Scalar(value), current)) return false;
        }
        return stored.Links.SetEquals(proposal.Links.Select(l => (l.Relation, l.Target)));
    }

    /// <summary>Closes every live record of a pack in one transaction; history stays.</summary>
    internal async Task<KnowledgePackImportResult> RemovePackAsync(string packId, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        var module = _resolution.Modules.Single(m => m.Id == packId);
        var request = PackRequest(packId, atUtc);
        try
        {
            return await _runner.WriteAsync(async transaction =>
            {
                await ReadyAsync(transaction, cancellationToken).ConfigureAwait(false);
                var live = await PackLiveAsync(transaction, module, cancellationToken).ConfigureAwait(false);
                var closed = 0;
                foreach (var key in live.Keys.OrderBy(k => KeyId(module.Id, k.Type, null, k.Key), StringComparer.Ordinal))
                    if (await CloseAsync(transaction, module, request, key, atUtc, cancellationToken).ConfigureAwait(false)) closed++;
                return new KnowledgePackImportResult { PackId = packId, Closed = closed };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (ModuleRecordRefusal refusal) { throw new KnowledgePackException([refusal.Message]); }
    }

    // A snapshot whose links reach into a dependency type that holds no records here yet is refused, in every store alike (review
    // 7, W9: Neo4j resolves a link as it writes, memory once every pack is mounted, so a work snapshot imported before the code
    // map lost its links here and kept them there). A dependency that holds records but not a link's target leaves that link out,
    // counted, as memory does.
    private async Task DependenciesHeldAsync(IAsyncQueryRunner runner, ModuleManifest module, IReadOnlyList<ModuleProposal> records, CancellationToken cancellationToken)
    {
        var relations = module.Ontology!.Relations.Where(r => r.To.Contains(':', StringComparison.Ordinal) && !r.To.StartsWith("core:", StringComparison.Ordinal))
            .Where(r => records.Any(p => p.Type == r.From && p.Links.Any(l => l.Relation == r.Name))).ToList();
        foreach (var relation in relations)
        {
            var label = _links.TargetLabel(module, relation);
            var held = await RowsAsync(runner, "MATCH (x:`" + label + "`) WHERE x.invalidated_at IS NULL RETURN 1 AS one LIMIT 1", new { }, cancellationToken)
                .ConfigureAwait(false);
            if (held.Count == 0)
                throw new ModuleRecordRefusal($"{module.Id} links into {relation.To}, which holds no records here yet: import {relation.To.Split(':')[0]} first.");
        }
    }

    /// <summary>The live records of a pack, by type and key, with the version that wrote each.</summary>
    internal async Task<IReadOnlyDictionary<(string Type, string Key), string?>> PackLiveAsync(IAsyncQueryRunner runner, ModuleManifest module,
        CancellationToken cancellationToken)
    {
        var live = new Dictionary<(string Type, string Key), string?>();
        foreach (var type in module.Ontology!.Types)
            foreach (var row in await RowsAsync(runner, ModuleRecordQueries.PackLive(type.Name), new { pack = module.Id }, cancellationToken).ConfigureAwait(false))
                live[(type.Name, row["key"].As<string>())] = row["version"]?.As<string>();
        return live;
    }

    private async Task<bool> CloseAsync(IAsyncQueryRunner runner, ModuleManifest module, IngestionRequest request, (string Type, string Key) key,
        DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        var proposal = new ModuleProposal { Type = key.Type, Key = key.Key, Kind = ProposalKind.Invalidate,
            Source = new() { Excerpt = "closed by the knowledge pack importer" } };
        var outcome = await WriteOneAsync(runner, module, request, new() { Index = 0, Proposal = proposal }, atUtc, cancellationToken).ConfigureAwait(false);
        return outcome.Status == ProposalWriteStatus.Invalidated;
    }

    private async Task ReadyAsync(IAsyncQueryRunner transaction, CancellationToken cancellationToken)
    {
        var readiness = await _migrator.PlanInAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (readiness.Problems.Count != 0 || readiness.Pending.Count != 0 || readiness.PendingNamespaces.Count != 0 || readiness.PendingBookkeeping.Count != 0)
            throw new ModuleRecordRefusal("Module migrations are pending or inconsistent; apply them explicitly before writes.");
    }

    // A pack writes shared records, owned by no one, under a session id that names the pack.
    private static IngestionRequest PackRequest(string packId, DateTimeOffset atUtc) =>
        new() { SessionId = "pack:" + packId, Owner = null, AtUtc = atUtc, Trigger = IngestionTrigger.OnRequest };

    private static async Task VerifySourcesAsync(IAsyncQueryRunner runner, IngestionRequest request,
        IReadOnlyList<AdmittedModuleProposal> proposals, CancellationToken cancellationToken)
    {
        var ids = proposals.SelectMany(p => p.Proposal.Source.MessageIds).Distinct(StringComparer.Ordinal).ToArray();
        var actual = await RowsAsync(runner, ModuleRecordQueries.Sources,
            new { ids, session = request.SessionId, owner = request.Owner }, cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            var supplied = request.Messages.Single(m => m.Id == id);
            var matches = actual.Where(r => r["id"].As<string>() == id && r["role"].As<string>() == supplied.Role && r["text"].As<string>() == supplied.Text).ToArray();
            if (matches.Length != 1) throw new ModuleRecordRefusal("Source message is absent or does not match the resolved owner/session, role and content.");
        }
    }

    private static async Task<Dictionary<string, object>> LockAsync(IAsyncQueryRunner runner, string module,
        ModuleType type, string key, string? owner, CancellationToken cancellationToken)
    {
        var keyId = KeyId(module, type.Name, owner, key);
        var rows = await RowsAsync(runner, ModuleRecordQueries.LockKey, new { keyId, module, type = type.Name, owner, key }, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1) throw new ModuleRecordRefusal("Module key identity is ambiguous.");
        var row = rows[0]["row"].As<Dictionary<string, object>>();
        if (Text(row, "module_id") != module || Text(row, "type") != type.Name || Text(row, "owner_id") != owner || Text(row, "key") != key
            || !row.TryGetValue("version", out var version) || version is not long number || number < 0)
            throw new ModuleRecordRefusal("Module key bookkeeping is inconsistent.");
        return row;
    }

    private async Task<ProposalOutcome> WriteOneAsync(IAsyncQueryRunner runner, ModuleManifest module,
        IngestionRequest request, AdmittedModuleProposal input, DateTimeOffset atUtc, CancellationToken cancellationToken, bool exact = false)
    {
        var proposal = input.Proposal; var type = module.Ontology!.Types.Single(t => t.Name == proposal.Type);
        var owner = type.Scope == "shared" ? null : request.Owner;
        var keyId = KeyId(module.Id, type.Name, owner, proposal.Key);
        var key = await LockAsync(runner, module.Id, type, proposal.Key, owner, cancellationToken).ConfigureAwait(false);
        var version = key["version"].As<long>();
        var previousId = version == 0 ? null : keyId + ":" + version.ToString(CultureInfo.InvariantCulture);
        if (Text(key, "current_id") is { } current && current != previousId) throw new ModuleRecordRefusal("Module key current reference does not match its last version.");
        var properties = proposal.Properties.ToDictionary(p => p.Key, p => Scalar(p.Value), StringComparer.Ordinal);
        var life = proposal.Life;
        string? Instant(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        var (validFrom, validTo, expiresAt, reviewBy) = (Instant(life?.ValidFromUtc), Instant(life?.ValidToUtc), Instant(life?.ExpiresAtUtc), Instant(life?.ReviewByUtc));
        var oldRows = previousId is null ? [] : await RowsAsync(runner, ModuleRecordQueries.Current(type),
            new { id = previousId, properties, now = atUtc.ToString("O"), validFrom, validTo, expiresAt, reviewBy, confidence = (long?)proposal.ConfidenceMicro }, cancellationToken).ConfigureAwait(false);
        if (previousId is not null && oldRows.Count != 1) throw new ModuleRecordRefusal("Module version history is incomplete.");
        var old = oldRows.SingleOrDefault();
        if (old is not null)
        {
            var record = old["row"].As<Dictionary<string, object>>();
            if (Text(record, "key_id") != keyId || Text(record, "key") != proposal.Key || Text(record, "owner_id") != owner
                || Text(record, "superseded_by") is not null)
                throw new ModuleRecordRefusal("Current record identity, owner or supersession does not match its key.");
        }
        var live = old is not null && old["live"].As<bool>();
        if ((Text(key, "current_id") is not null) != live) throw new ModuleRecordRefusal("Module key liveness is inconsistent.");
        if (proposal.Kind == ProposalKind.Invalidate)
        {
            if (!live) return Outcome(input.Index, ProposalWriteStatus.NoOp, previousId);
            if (old!["newer"].As<bool>()) throw new ModuleRecordRefusal("Write clock precedes the current version.");
            await OneAsync(runner, ModuleRecordQueries.Invalidate(type.Name), new { id = previousId, keyId, now = atUtc.ToString("O") }, cancellationToken).ConfigureAwait(false);
            return Outcome(input.Index, ProposalWriteStatus.Invalidated, previousId);
        }
        var links = await _links.ResolveAsync(runner, module, proposal, previousId, owner, cancellationToken, exact).ConfigureAwait(false);
        if (links.LeftOut != 0) Interlocked.Add(ref _linksLeftOut, links.LeftOut);
        if (live && old!["equal"].As<bool>() && !links.Changed) return Outcome(input.Index, ProposalWriteStatus.NoOp, previousId);
        if (old is not null && old["newer"].As<bool>()) throw new ModuleRecordRefusal("Write clock precedes the last version.");
        var nextVersion = checked(version + 1); var id = keyId + ":" + nextVersion.ToString(CultureInfo.InvariantCulture);
        var pack = proposal.Source.Pack;
        await OneAsync(runner, ModuleRecordQueries.Create(type), new { id, keyId, key = proposal.Key, owner,
            now = atUtc.ToString("O"), session = request.SessionId, sources = proposal.Source.MessageIds.ToArray(), excerpt = proposal.Source.Excerpt,
            pack = pack?.Id, packVersion = pack?.Version, packDigest = pack?.Digest, packFile = pack?.File, packLine = pack?.Line,
            carrier = pack?.Carrier?.Id, carrierVersion = pack?.Carrier?.Version, carrierDigest = pack?.Carrier?.Digest, carrierSigner = pack?.Carrier?.Signer,
            validFrom, validTo, expiresAt, reviewBy, confidence = (long?)proposal.ConfidenceMicro, properties }, cancellationToken).ConfigureAwait(false);
        foreach (var link in links.Links)
            await OneAsync(runner, ModuleRecordQueries.Link(type.Name, link.Relation, link.TargetLabel), new { id, target = link.TargetId, owner }, cancellationToken).ConfigureAwait(false);
        if (previousId is not null)
            await OneAsync(runner, ModuleRecordQueries.Supersede(type.Name), new { previous = previousId, id, now = atUtc.ToString("O") }, cancellationToken).ConfigureAwait(false);
        await OneAsync(runner, ModuleRecordQueries.SetCurrent, new { keyId, version = nextVersion, id }, cancellationToken).ConfigureAwait(false);
        return Outcome(input.Index, ProposalWriteStatus.Stored, id);
    }

    private static object Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!, JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(), _ => throw new ModuleRecordRefusal("Unsupported stored scalar.")
    };
    private static string KeyId(string module, string type, string? owner, string key) => "module:" +
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { module, type, owner, key }))).ToLowerInvariant();
    private static ProposalOutcome Outcome(int index, ProposalWriteStatus status, string? id) => new() { Index = index, Status = status, RecordId = id };
    private static IReadOnlyList<ProposalOutcome> Refused(IReadOnlyList<AdmittedModuleProposal> proposals, string error) =>
        proposals.Select(p => new ProposalOutcome { Index = p.Index, Status = ProposalWriteStatus.Refused, Errors = [error] }).ToList().AsReadOnly();
}
