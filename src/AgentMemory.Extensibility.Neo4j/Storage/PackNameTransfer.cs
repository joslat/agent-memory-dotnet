// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Queries;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Shared;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>
/// Transfers what a store holds of a removed pack id to a new pack id (PACK-NAME-TAKEOVER, decision D-06, 22 Nov 2026): the
/// schema names it owns, its records and its erasures' tombstones. Checked first, reading only; then moved in one transaction
/// that checks again, compares the records' counts and walk before and after, switches the names, and asks the new pack's own
/// migration plan whether it owns them. Any difference throws, and the transaction leaves everything as it was.
/// </summary>
[Experimental("AMEXT001")]
internal sealed class PackNameTransfer
{
    private readonly INeo4jTransactionRunner _runner;
    private readonly string _from;
    private readonly ModuleManifest _to;
    private readonly ModuleResolution _resolution;
    private readonly ModuleMigrationCompilation _compilation;
    private readonly string _actor;
    private readonly DateTimeOffset _atUtc;
    private readonly TimeProvider _clock;

    internal PackNameTransfer(INeo4jTransactionRunner runner, string fromPackId, ModuleResolution resolution, string actor, DateTimeOffset atUtc, TimeProvider clock)
    {
        _runner = runner;
        _from = fromPackId;
        _resolution = resolution;
        _to = resolution.Modules.Single();
        _compilation = ModuleMigrationCompilation.Compile(resolution, []);
        _actor = actor;
        _atUtc = atUtc;
        _clock = clock;
    }

    private string Now => _atUtc.ToString("O", CultureInfo.InvariantCulture);

    // What the old id holds that moves: its names (namespace keys), its schema steps (keys) and the labels its records are under.
    private sealed record Holding(IReadOnlyList<string> Names, IReadOnlyList<string> Steps, IReadOnlyList<string> Labels);

    /// <summary>Every check, reading only; throws <see cref="KnowledgePackException"/> with every problem found.</summary>
    internal Task<bool> CheckAsync(CancellationToken cancellationToken) =>
        _runner.ReadAsync(async transaction => { await InspectAsync(transaction, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken);

    /// <summary>The move, in one transaction.</summary>
    internal async Task<KnowledgePackTransferResult> MoveAsync(CancellationToken cancellationToken)
    {
        try { return await _runner.WriteAsync(transaction => MoveInAsync(transaction, cancellationToken), cancellationToken).ConfigureAwait(false); }
        catch (ModuleRecordRefusal refusal) { throw new KnowledgePackException([refusal.Message]); }
    }

    private async Task<KnowledgePackTransferResult> MoveInAsync(IAsyncQueryRunner transaction, CancellationToken cancellationToken)
    {
        var sdk = ModuleBookkeepingSchema.NamespaceLedgerId;
        // The SDK migration lock, as every compliant migrator takes it before checking and reserving names.
        await RowsAsync(transaction, ModuleLedgerQueries.Lock, new { sdkId = sdk, actor = _actor, now = Now }, cancellationToken).ConfigureAwait(false);
        var holding = await InspectAsync(transaction, cancellationToken).ConfigureAwait(false);

        // 1. The records and their walk as they are, then moved to the new id: each keeps the id that wrote it.
        var before = await WalkAsync(transaction, holding.Labels, _from, cancellationToken).ConfigureAwait(false);
        var tombstones = await CountAsync(transaction, PackOwnershipQueries.Tombstones, new { pack = _from }, cancellationToken).ConfigureAwait(false);
        long records = 0;
        foreach (var label in holding.Labels)
            records += await CountAsync(transaction, PackOwnershipQueries.Reattribute(label), new { from = _from, to = _to.Id }, cancellationToken).ConfigureAwait(false);
        var buried = await CountAsync(transaction, PackOwnershipQueries.MoveTombstones, new { from = _from, to = _to.Id }, cancellationToken).ConfigureAwait(false);

        // 2. The same counts and the same walk after the move, or nothing moves.
        var after = await WalkAsync(transaction, holding.Labels, _to.Id, cancellationToken).ConfigureAwait(false);
        var left = await WalkAsync(transaction, holding.Labels, _from, cancellationToken).ConfigureAwait(false);
        if (records != before.Records.Count || left.Records.Count != 0 || buried != tombstones || !before.SameAs(after))
            throw new KnowledgePackException([$"The records of {_from} did not keep their counts and walk when they moved ({before.Records.Count} records, "
                + $"{before.Relationships.Count} relationships and {tombstones} tombstones before; {after.Records.Count}, {after.Relationships.Count} and {buried} "
                + "after); nothing was transferred."]);

        // 3. The names and the schema steps switch to the new id; the store keeps who asked and when.
        var names = await CountAsync(transaction, PackOwnershipQueries.MoveNames, new { sdk, from = _from, to = _to.Id }, cancellationToken).ConfigureAwait(false);
        var steps = await CountAsync(transaction, PackOwnershipQueries.MoveSteps, new { from = _from, to = _to.Id }, cancellationToken).ConfigureAwait(false);
        if (names != holding.Names.Count || steps != holding.Steps.Count)
            throw new KnowledgePackException([$"The schema names of {_from} changed while they moved; nothing was transferred."]);
        await OneAsync(transaction, PackOwnershipQueries.RecordTransfer, new
        {
            sdk, key = "transfer/" + _from + "/" + _to.Id + "/" + Now, to = _to.Id, actor = _actor, now = Now,
        }, cancellationToken).ConfigureAwait(false);

        // 4. The new pack's own plan, in this transaction: it must own every name it declares, with no problem.
        var plan = await new ModuleMigrator(_runner, _resolution, _clock).PlanInAsync(transaction, cancellationToken).ConfigureAwait(false);
        if (plan.Problems.Count != 0)
            throw new KnowledgePackException([.. plan.Problems.Select(p => $"After the transfer, {_to.Id} would not own its schema: {p}; nothing was transferred.")]);

        return new KnowledgePackTransferResult
        {
            FromPackId = _from, ToPackId = _to.Id, Actor = _actor, AtUtc = _atUtc, Names = (int)names, SchemaSteps = (int)steps, Records = (int)records,
            Relationships = before.Relationships.Count, Tombstones = (int)buried,
        };
    }

    private async Task<Holding> InspectAsync(IAsyncQueryRunner transaction, CancellationToken cancellationToken)
    {
        var sdk = ModuleBookkeepingSchema.NamespaceLedgerId;
        var names = await RowsAsync(transaction, PackOwnershipQueries.OwnedNames, new { sdk, pack = _from }, cancellationToken).ConfigureAwait(false);
        if (names.Count == 0) throw new KnowledgePackException([$"The store holds no schema name of {_from}: there is nothing to transfer."]);
        var problems = new List<string>();
        var taken = await RowsAsync(transaction, PackOwnershipQueries.OwnedNames, new { sdk, pack = _to.Id }, cancellationToken).ConfigureAwait(false);
        var theirs = await RowsAsync(transaction, PackOwnershipQueries.Steps, new { pack = _to.Id }, cancellationToken).ConfigureAwait(false);
        if (taken.Count != 0 || theirs.Count != 0)
            problems.Add($"{_to.Id} already holds {taken.Count} schema names and {theirs.Count} schema steps here; names are transferred only to an id that holds none.");
        var steps = await RowsAsync(transaction, PackOwnershipQueries.Steps, new { pack = _from }, cancellationToken).ConfigureAwait(false);
        foreach (var row in names.Where(n => n["state"]?.As<string>() != "owned"))
            problems.Add($"The name {row["key"].As<string>()} of {_from} is {row["state"]?.As<string>() ?? "unrecorded"}, not owned.");
        foreach (var row in steps.Where(s => s["state"]?.As<string>() != "applied"))
            problems.Add($"The schema step {row["key"].As<string>()} of {_from} is {row["state"]?.As<string>() ?? "unrecorded"}, not applied.");

        // The new pack must declare every name the old id owns, and every schema step it applied, as the same statement.
        var claims = _compilation.Claims.Select(c => c.Key).ToHashSet(StringComparer.Ordinal);
        var undeclared = names.Select(n => n["key"].As<string>()).Where(k => !claims.Contains(k)).Select(k => k[(k.LastIndexOf('/') + 1)..]).ToList();
        if (undeclared.Count != 0)
            problems.Add($"{_to.Id} does not declare {string.Join(", ", undeclared)}: it must declare every name {_from} owns.");
        var declared = _compilation.Steps.Select(s => s.Definition).ToDictionary(s => s.Key, s => s.Sha256, StringComparer.Ordinal);
        foreach (var row in steps)
        {
            var key = row["key"].As<string>();
            if (!declared.TryGetValue(key, out var hash)) problems.Add($"{_to.Id} does not declare the schema step {key} of {_from}.");
            else if (row["sha256"]?.As<string>() != hash) problems.Add($"{_to.Id} declares the schema step {key} differently from {_from}.");
        }

        // Only a removed pack's own shared records move: none live, none written by anyone else, none owned or carried.
        var labels = names.Select(n => n["key"].As<string>()).Where(k => k.StartsWith(PackOwnershipQueries.LabelClaim, StringComparison.Ordinal))
            .Select(k => k[PackOwnershipQueries.LabelClaim.Length..]).Order(StringComparer.Ordinal).ToList();
        if (labels.Any(l => l.Length == 0 || l.Contains('`', StringComparison.Ordinal)))
            throw new KnowledgePackException(["Module namespace ledger names a label that is not a schema name."]);
        foreach (var label in labels)
        {
            var census = (await RowsAsync(transaction, PackOwnershipQueries.Census(label), new { pack = _from }, cancellationToken).ConfigureAwait(false)).Single();
            var (total, mine, live, scoped) = (census["total"].As<long>(), census["mine"].As<long>(), census["live"].As<long>(), census["scoped"].As<long>());
            if (live != 0) problems.Add($"{_from} still has {live} live {label} records: remove it first.");
            if (total != mine) problems.Add($"{label} holds {total - mine} records {_from} did not write; only a removed pack's own records are transferred.");
            if (scoped != 0) problems.Add($"{label} holds {scoped} records of {_from} that an owner holds or another pack carried; only shared pack records are transferred.");
        }
        var open = await CountAsync(transaction, PackOwnershipQueries.LiveKeys, new { pack = _to.Id }, cancellationToken).ConfigureAwait(false);
        if (open != 0) problems.Add($"{_to.Id} already has {open} live records here; names are transferred only to an id that holds none.");
        var buried = await CountAsync(transaction, PackOwnershipQueries.Tombstones, new { pack = _to.Id }, cancellationToken).ConfigureAwait(false);
        if (buried != 0) problems.Add($"{_to.Id} already has {buried} erasure tombstones here; names are transferred only to an id that holds none.");
        if (problems.Count != 0) throw new KnowledgePackException(problems);
        return new([.. names.Select(n => n["key"].As<string>())], [.. steps.Select(s => s["key"].As<string>())], labels);
    }

    // Every record a pack id is the source of, by label and id, with its relationships (type, direction and the node at the other
    // end), and the relationships themselves.
    private sealed record Walk(Dictionary<string, string[]> Records, HashSet<string> Relationships)
    {
        internal bool SameAs(Walk other) => Records.Count == other.Records.Count && Relationships.SetEquals(other.Relationships)
            && Records.All(record => other.Records.TryGetValue(record.Key, out var edges) && edges.SequenceEqual(record.Value, StringComparer.Ordinal));
    }

    private static async Task<Walk> WalkAsync(IAsyncQueryRunner transaction, IReadOnlyList<string> labels, string pack, CancellationToken cancellationToken)
    {
        var walk = new Walk(new(StringComparer.Ordinal), new(StringComparer.Ordinal));
        foreach (var label in labels)
            foreach (var row in await RowsAsync(transaction, PackOwnershipQueries.Walk(label), new { pack }, cancellationToken).ConfigureAwait(false))
            {
                walk.Records[label + "/" + row["id"].As<string>()] = [.. row["edges"].As<List<string>>().Order(StringComparer.Ordinal)];
                walk.Relationships.UnionWith(row["relationships"].As<List<string>>());
            }
        return walk;
    }

    private static async Task<long> CountAsync(IAsyncQueryRunner transaction, string query, object parameters, CancellationToken cancellationToken) =>
        (await RowsAsync(transaction, query, parameters, cancellationToken).ConfigureAwait(false)).Single()["n"].As<long>();
}
