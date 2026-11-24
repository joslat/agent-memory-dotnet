// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Queries;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Shared;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>
/// A pack id's records read from what the store itself holds of it, without the pack's manifest: the labels its id owns in the
/// SDK namespace ledger and the records it wrote under them (PACK-REMOVE-MISSING-ARCHIVE).
/// </summary>
[Experimental("AMEXT001")]
internal static class PackOwnership
{
    /// <summary>
    /// Closes every live record a pack wrote, in one transaction, as a removal by its manifest does: each key locked in key order,
    /// checked against its record, and its current reference cleared. Null when the store holds no name of the id.
    /// </summary>
    internal static async Task<KnowledgePackImportResult?> RemoveByIdAsync(INeo4jTransactionRunner runner, string packId, DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        try { return await CloseByIdAsync(runner, packId, atUtc, cancellationToken).ConfigureAwait(false); }
        catch (ModuleRecordRefusal refusal) { throw new KnowledgePackException([refusal.Message]); }
    }

    private static Task<KnowledgePackImportResult?> CloseByIdAsync(INeo4jTransactionRunner runner, string packId, DateTimeOffset atUtc,
        CancellationToken cancellationToken)
    {
        var now = atUtc.ToString("O", CultureInfo.InvariantCulture);
        return runner.WriteAsync<KnowledgePackImportResult?>(async transaction =>
        {
            if (await OwnedNamesAsync(transaction, packId, cancellationToken).ConfigureAwait(false) is not { } owned) return null;
            var closed = await CloseLiveAsync(transaction, packId, owned.Labels, now, cancellationToken).ConfigureAwait(false);
            return new KnowledgePackImportResult { PackId = packId, Closed = closed };
        }, cancellationToken);
    }

    /// <summary>
    /// What a version of a pack no longer defines of what the store holds live of its id (the host lifecycle, R-HOST-062): the
    /// labels and relation types the id owns that the version does not define and its live records still use, and the property
    /// names of its live records under each type the version keeps. A name the id keeps after its records closed is not a drop.
    /// </summary>
    internal static async Task<KnowledgePackDrops> DropsAsync(IAsyncQueryRunner transaction, ModuleManifest pack, CancellationToken cancellationToken)
    {
        if (await OwnedNamesAsync(transaction, pack.Id, cancellationToken).ConfigureAwait(false) is not { } owned) return KnowledgePackDrops.None;
        var types = (pack.Ontology?.Types ?? []).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        var relations = (pack.Ontology?.Relations ?? []).Select(r => r.Name).ToHashSet(StringComparer.Ordinal);
        var liveTypes = new List<string>();
        foreach (var label in owned.Labels.Where(l => !types.Contains(l)))
            if ((await RowsAsync(transaction, PackOwnershipQueries.AnyLive(label), new { pack = pack.Id }, cancellationToken).ConfigureAwait(false)).Count > 0)
                liveTypes.Add(label);
        var liveRelations = new List<string>();
        foreach (var relation in owned.Relations.Where(r => !relations.Contains(r)))
            if ((await RowsAsync(transaction, PackOwnershipQueries.AnyLiveRelation(relation), new { pack = pack.Id }, cancellationToken).ConfigureAwait(false)).Count > 0)
                liveRelations.Add(relation);
        var live = new Dictionary<string, IReadOnlyCollection<string>>(StringComparer.Ordinal);
        foreach (var type in (pack.Ontology?.Types ?? []).Where(t => owned.Labels.Contains(t.Name, StringComparer.Ordinal)))
        {
            var rows = await RowsAsync(transaction, PackOwnershipQueries.LiveProperties(type.Name), new { pack = pack.Id }, cancellationToken).ConfigureAwait(false);
            live[type.Name] = [.. rows.Select(row => row["name"].As<string>())];
        }
        return KnowledgePackDrops.Compare(pack, liveTypes, liveRelations, live);
    }

    /// <summary>
    /// The live records of a pack, by type and key, that carry what a version dropped (<paramref name="drops"/>): a property its
    /// type no longer declares, or a link of a relation it no longer defines. Each is written again as a new version without it,
    /// even when everything the version declares is equal.
    /// </summary>
    internal static async Task<IReadOnlySet<(string Type, string Key)>> CarryingAsync(IAsyncQueryRunner transaction, ModuleManifest pack,
        KnowledgePackDrops drops, CancellationToken cancellationToken)
    {
        var carrying = new HashSet<(string Type, string Key)>();
        var types = (pack.Ontology?.Types ?? []).Select(t => t.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var dropped in drops.Properties)
        {
            var dot = dropped.IndexOf('.', StringComparison.Ordinal);
            var (type, property) = (dropped[..dot], dropped[(dot + 1)..]);
            // A stored name that could leave its quotes is not read.
            if (property.Contains('`', StringComparison.Ordinal)) throw new KnowledgePackException(["A stored record carries a property that is not a schema name."]);
            foreach (var row in await RowsAsync(transaction, PackOwnershipQueries.LiveWithProperty(type, property), new { pack = pack.Id }, cancellationToken).ConfigureAwait(false))
                carrying.Add((type, row["key"].As<string>()));
        }
        foreach (var relation in drops.Relations)
            foreach (var row in await RowsAsync(transaction, PackOwnershipQueries.LiveWithRelation(relation), new { pack = pack.Id }, cancellationToken).ConfigureAwait(false))
                foreach (var label in row["labels"].As<List<string>>().Where(types.Contains))
                    carrying.Add((label, row["key"].As<string>()));
        return carrying;
    }

    /// <summary>
    /// Closes a pack's live records under the labels given, in one transaction's work, as a removal by its manifest closes them:
    /// each key locked in key order, checked against its record, and its current reference cleared.
    /// </summary>
    internal static async Task<int> CloseLiveAsync(IAsyncQueryRunner transaction, string packId, IEnumerable<string> labels, string now,
        CancellationToken cancellationToken)
    {
        var live = new List<(string Label, string Id, string KeyId, string Key, bool Newer, string? Owner, string? SupersededBy)>();
        foreach (var label in labels)
            foreach (var row in await RowsAsync(transaction, PackOwnershipQueries.LiveRecords(label), new { pack = packId, now }, cancellationToken).ConfigureAwait(false))
                live.Add((label, row["id"].As<string>(), row["keyId"]?.As<string>() ?? "", row["key"]?.As<string>() ?? "", row["newer"].As<bool>(),
                    row["owner"]?.As<string>(), row["supersededBy"]?.As<string>()));
        var closed = 0;
        foreach (var record in live.OrderBy(r => r.KeyId, StringComparer.Ordinal))
        {
            var rows = await RowsAsync(transaction, PackOwnershipQueries.LockHeldKey, new { keyId = record.KeyId }, cancellationToken).ConfigureAwait(false);
            if (rows.Count != 1) throw new KnowledgePackException(["Module key bookkeeping is inconsistent."]);
            var key = rows[0]["row"].As<Dictionary<string, object>>();
            // The checks a removal by manifest makes before it closes a record (Neo4jModuleStore.WriteOneAsync).
            if (record.KeyId != Neo4jModuleStore.KeyId(packId, record.Label, null, record.Key) || Text(key, "module_id") != packId
                || Text(key, "type") != record.Label || Text(key, "owner_id") is not null || Text(key, "key") != record.Key
                || !key.TryGetValue("version", out var version) || version is not long number || number < 1)
                throw new KnowledgePackException(["Module key bookkeeping is inconsistent."]);
            if (Text(key, "current_id") != record.KeyId + ":" + number.ToString(CultureInfo.InvariantCulture))
                throw new KnowledgePackException(["Module key current reference does not match its last version."]);
            if (record.Id != Text(key, "current_id") || record.Owner is not null || record.SupersededBy is not null)
                throw new KnowledgePackException(["Current record identity, owner or supersession does not match its key."]);
            if (record.Newer) throw new KnowledgePackException(["Write clock precedes the current version."]);
            await OneAsync(transaction, ModuleRecordQueries.Invalidate(record.Label), new { id = record.Id, keyId = record.KeyId, now }, cancellationToken)
                .ConfigureAwait(false);
            closed++;
        }
        return closed;
    }

    // The labels and relation types a pack id owns, each in ordinal order; null when it owns no name here. Its names and schema
    // steps must be complete, as a removal by manifest requires its migrations applied.
    private static async Task<(IReadOnlyList<string> Labels, IReadOnlyList<string> Relations)?> OwnedNamesAsync(IAsyncQueryRunner transaction,
        string packId, CancellationToken cancellationToken)
    {
        var names = await RowsAsync(transaction, PackOwnershipQueries.OwnedNames, new { sdk = ModuleBookkeepingSchema.NamespaceLedgerId, pack = packId },
            cancellationToken).ConfigureAwait(false);
        if (names.Count == 0) return null;
        var steps = await RowsAsync(transaction, PackOwnershipQueries.Steps, new { pack = packId }, cancellationToken).ConfigureAwait(false);
        if (names.Any(n => n["state"]?.As<string>() != "owned") || steps.Any(s => s["state"]?.As<string>() != "applied"))
            throw new KnowledgePackException(["Module migrations are pending or inconsistent; apply them explicitly before writes."]);
        List<string> Claimed(string claim) => [.. names.Select(n => n["key"].As<string>()).Where(k => k.StartsWith(claim, StringComparison.Ordinal))
            .Select(k => k[claim.Length..]).Order(StringComparer.Ordinal)];
        var labels = Claimed(PackOwnershipQueries.LabelClaim);
        var relations = Claimed(PackOwnershipQueries.RelationClaim);
        // A name was validated when it was claimed; one that could leave its quotes is not read.
        if (labels.Concat(relations).Any(l => l.Length == 0 || l.Contains('`', StringComparison.Ordinal)))
            throw new KnowledgePackException(["Module namespace ledger names a label that is not a schema name."]);
        return (labels, relations);
    }
}
