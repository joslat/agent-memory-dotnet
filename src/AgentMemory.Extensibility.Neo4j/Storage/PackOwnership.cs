// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
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
            var labels = await OwnedLabelsAsync(transaction, packId, cancellationToken).ConfigureAwait(false);
            if (labels is null) return null;
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
            return new KnowledgePackImportResult { PackId = packId, Closed = closed };
        }, cancellationToken);
    }

    // The labels a pack id owns, in ordinal order; null when it owns no name here. Its names and schema steps must be complete, as
    // a removal by manifest requires its migrations applied.
    private static async Task<IReadOnlyList<string>?> OwnedLabelsAsync(IAsyncQueryRunner transaction, string packId, CancellationToken cancellationToken)
    {
        var names = await RowsAsync(transaction, PackOwnershipQueries.OwnedNames, new { sdk = ModuleBookkeepingSchema.NamespaceLedgerId, pack = packId },
            cancellationToken).ConfigureAwait(false);
        if (names.Count == 0) return null;
        var steps = await RowsAsync(transaction, PackOwnershipQueries.Steps, new { pack = packId }, cancellationToken).ConfigureAwait(false);
        if (names.Any(n => n["state"]?.As<string>() != "owned") || steps.Any(s => s["state"]?.As<string>() != "applied"))
            throw new KnowledgePackException(["Module migrations are pending or inconsistent; apply them explicitly before writes."]);
        var labels = names.Select(n => n["key"].As<string>()).Where(k => k.StartsWith(PackOwnershipQueries.LabelClaim, StringComparison.Ordinal))
            .Select(k => k[PackOwnershipQueries.LabelClaim.Length..]).Order(StringComparer.Ordinal).ToList();
        // A name was validated when it was claimed; one that could leave its quotes is not read.
        if (labels.Any(l => l.Length == 0 || l.Contains('`', StringComparison.Ordinal)))
            throw new KnowledgePackException(["Module namespace ledger names a label that is not a schema name."]);
        return labels;
    }
}
