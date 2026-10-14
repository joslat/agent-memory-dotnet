using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

[Experimental("AMEXT001")]
internal static class ModuleTargetResolver
{
    internal static async Task<string?> FollowAsync(IAsyncQueryRunner runner, string label, string id,
        string? owner, bool includeShared, DateTimeOffset? systemAsOfUtc, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 16; depth++)
        {
            if (!visited.Add(id)) return null;
            var rows = await RowsAsync(runner, ModuleReadQueries.Target(label),
                new { target = id, owner, shared = includeShared, asOf = systemAsOfUtc?.ToString("O") }, cancellationToken).ConfigureAwait(false);
            if (rows.Count != 1) return null;
            var row = rows[0]["row"].As<Dictionary<string, object>>();
            if (systemAsOfUtc is not null)
            {
                if (rows[0]["unborn"].As<bool>()) return null;
                if (rows[0]["historicalLive"].As<bool>()) return Text(row, "id");
            }
            var next = Text(row, label == "Entity" ? "merged_into" : "superseded_by");
            if (!string.IsNullOrWhiteSpace(next)) { id = next; continue; }
            return systemAsOfUtc is not null || row.ContainsKey("invalidated_at") ? null : Text(row, "id");
        }
        return null;
    }
}
