using AgentMemory.Extensibility.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

internal sealed record ModuleDatabaseObject(string Name, string Kind, string EntityType,
    IReadOnlyList<string> Labels, IReadOnlyList<string> Properties, bool IsConstraint,
    string? OwningConstraint, string? State);

internal sealed record ModuleLedgerEntry(string ModuleId, string Key, string? Hash, string? State, string? Owner,
    long? CompletedStatements = null, bool InvalidProgress = false)
{
    internal static ModuleLedgerEntry FromRow(IReadOnlyDictionary<string, object> row)
    {
        string? Get(string key) => row.TryGetValue(key, out var v) ? v?.As<string>() : null;
        row.TryGetValue("completed_statements", out var raw);
        long? progress = raw switch { long value => value, int value => value, _ => null };
        return new(Get("module_id") ?? "", Get("key") ?? "", Get("sha256"), Get("state"), Get("owner_module_id"),
            progress, raw is not null && progress is null);
    }
}

internal sealed record ModuleDatabaseSnapshot(
    IReadOnlyList<ModuleDatabaseObject> Objects, IReadOnlySet<string> Labels,
    IReadOnlySet<string> Relations, IReadOnlyList<ModuleLedgerEntry> Entries)
{
    internal static async Task<ModuleDatabaseSnapshot> ReadAsync(IAsyncQueryRunner runner,
        object parameters, CancellationToken cancellationToken)
    {
        var objects = new List<ModuleDatabaseObject>();
        foreach (var (query, isConstraint) in new[] { (ModuleLedgerQueries.Constraints, true), (ModuleLedgerQueries.Indexes, false) })
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cursor = await runner.RunAsync(query).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            objects.AddRange(rows.Select(r => new ModuleDatabaseObject(r["name"].As<string>(),
                r["type"].As<string>(), r["entityType"].As<string>(),
                r["labelsOrTypes"]?.As<List<string>>() ?? [], r["properties"]?.As<List<string>>() ?? [], isConstraint,
                isConstraint ? null : r["owningConstraint"]?.As<string>(),
                isConstraint ? null : r["state"]?.As<string>())));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var labelCursor = await runner.RunAsync(ModuleLedgerQueries.Labels).ConfigureAwait(false);
        var labels = (await labelCursor.ToListAsync().ConfigureAwait(false)).Select(r => r["label"].As<string>()).ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        var relationCursor = await runner.RunAsync(ModuleLedgerQueries.Relations).ConfigureAwait(false);
        var relations = (await relationCursor.ToListAsync().ConfigureAwait(false)).Select(r => r["relationshipType"].As<string>()).ToHashSet(StringComparer.Ordinal);
        cancellationToken.ThrowIfCancellationRequested();
        var ledgerCursor = await runner.RunAsync(ModuleLedgerQueries.Ledger, parameters).ConfigureAwait(false);
        var entries = (await ledgerCursor.ToListAsync().ConfigureAwait(false))
            .Select(r => ModuleLedgerEntry.FromRow(r["row"].As<Dictionary<string, object>>())).ToArray();
        return new(objects.AsReadOnly(), labels, relations, Array.AsReadOnly(entries));
    }
}
