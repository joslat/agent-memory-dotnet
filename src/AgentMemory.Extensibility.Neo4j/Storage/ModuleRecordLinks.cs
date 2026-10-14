using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

internal sealed record ResolvedModuleLink(string Relation, string TargetId, string TargetLabel);

[Experimental("AMEXT001")]
internal sealed class ModuleRecordLinks(ModuleResolution resolution)
{
    internal async Task<(IReadOnlyList<ResolvedModuleLink> Links, bool Changed)> ResolveAsync(IAsyncQueryRunner runner,
        ModuleManifest module, ModuleProposal proposal, string? previousId, string? owner, CancellationToken cancellationToken)
    {
        var relations = module.Ontology!.Relations.Where(r => r.From == proposal.Type).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var original = previousId is null ? [] : await RowsAsync(runner, ModuleRecordQueries.Links(proposal.Type),
            new { id = previousId, relations = relations.Keys.ToArray() }, cancellationToken).ConfigureAwait(false);
        var originalKeys = original.Select(r => (r["relation"].As<string>(), r["target"].As<string>())).ToHashSet();
        var final = new Dictionary<(string, string), ResolvedModuleLink>();
        foreach (var row in original)
        {
            var relation = relations[row["relation"].As<string>()];
            if (relation.Max == 1 && proposal.Links.Any(l => l.Relation == relation.Name)) continue;
            var label = TargetLabel(module, relation);
            var id = await FollowAsync(runner, label, row["target"].As<string>(), owner, cancellationToken).ConfigureAwait(false);
            if (id is not null) final[(relation.Name, id)] = new(relation.Name, id, label);
        }
        foreach (var link in proposal.Links)
        {
            var relation = relations[link.Relation]; var label = TargetLabel(module, relation);
            var rows = await RowsAsync(runner, ModuleRecordQueries.Target(label, link.TargetKind == LinkTargetKind.EntityName, label != "Entity"),
                new { target = link.Target, owner }, cancellationToken).ConfigureAwait(false);
            if (rows.Count > 64) throw new ModuleRecordRefusal("Target lookup exceeds the bounded candidate count.");
            var targets = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in rows)
            {
                var candidate = row["row"].As<Dictionary<string, object>>();
                var id = await FollowAsync(runner, label, Text(candidate, "id")!, owner, cancellationToken).ConfigureAwait(false);
                if (id is not null) targets.Add(id);
            }
            if (targets.Count != 1) throw new ModuleRecordRefusal("Target is absent, outside scope, invalidated or ambiguous: " + relation.Name);
            var target = targets.Single(); final[(relation.Name, target)] = new(relation.Name, target, label);
        }
        foreach (var relation in relations.Values)
        {
            var count = final.Values.Count(l => l.Relation == relation.Name);
            if (relation.Required && count == 0) throw new ModuleRecordRefusal("Required live anchor is missing: " + relation.Name);
            if (relation.Max is { } max && count > max) throw new ModuleRecordRefusal("Final link cardinality exceeds the declared maximum: " + relation.Name);
        }
        return (final.Values.OrderBy(l => l.Relation, StringComparer.Ordinal).ThenBy(l => l.TargetId, StringComparer.Ordinal).ToList().AsReadOnly(),
            !originalKeys.SetEquals(final.Keys));
    }

    private string TargetLabel(ModuleManifest module, ModuleRelation relation)
    {
        if (relation.To == "core:Entity") return "Entity";
        var parts = relation.To.Split(':');
        if (parts.Length == 1) return relation.To;
        var dependency = resolution.Modules.SingleOrDefault(m => m.Id == parts[0]);
        if (!module.Modules.ContainsKey(parts[0]) || dependency?.Ontology?.Types.All(t => t.Name != parts[1]) != false)
            throw new ModuleRecordRefusal("Link dependency is not bound to this store.");
        return parts[1];
    }

    private static async Task<string?> FollowAsync(IAsyncQueryRunner runner, string label, string id,
        string? owner, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < 16; depth++)
        {
            if (!visited.Add(id)) return null;
            var rows = await RowsAsync(runner, ModuleRecordQueries.Target(label, false, false), new { target = id, owner }, cancellationToken).ConfigureAwait(false);
            if (rows.Count != 1) return null;
            var row = rows[0]["row"].As<Dictionary<string, object>>();
            var next = Text(row, label == "Entity" ? "merged_into" : "superseded_by");
            if (!string.IsNullOrWhiteSpace(next)) { id = next; continue; }
            return row.ContainsKey("invalidated_at") ? null : Text(row, "id");
        }
        return null;
    }
}
