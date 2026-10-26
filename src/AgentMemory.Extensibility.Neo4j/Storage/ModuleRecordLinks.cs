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
    // exact: the proposal's links are all of the record's links (a knowledge pack or a snapshot holds every record whole), so a
    // link the last version had and the proposal does not is dropped. Otherwise a proposal adds to the links its record already
    // has, as a turn's writer does. In exact mode a link into a dependency's records whose target is not in the store is left
    // out and counted (CODING-hierarchy: a work item names every file its commits changed; the code map holds the source files);
    // a turn's write still refuses it.
    internal async Task<(IReadOnlyList<ResolvedModuleLink> Links, bool Changed, int LeftOut)> ResolveAsync(IAsyncQueryRunner runner,
        ModuleManifest module, ModuleProposal proposal, string? previousId, string? owner, CancellationToken cancellationToken, bool exact = false)
    {
        var relations = module.Ontology!.Relations.Where(r => r.From == proposal.Type).ToDictionary(r => r.Name, StringComparer.Ordinal);
        var original = previousId is null ? [] : await RowsAsync(runner, ModuleRecordQueries.Links(proposal.Type),
            new { id = previousId, relations = relations.Keys.ToArray() }, cancellationToken).ConfigureAwait(false);
        // A link is the same link while it reaches the same live target: a target that only gained a version is not a change
        // to this record (CMP-2a: a code map commit would otherwise re-version every test of a changed type). A target that
        // closed, or a link added or dropped, still is.
        var reached = new HashSet<(string, string)>();
        var unreachable = 0;
        var leftOut = 0;
        var final = new Dictionary<(string, string), ResolvedModuleLink>();
        foreach (var row in original)
        {
            var relation = relations[row["relation"].As<string>()];
            var label = TargetLabel(module, relation);
            var id = await FollowAsync(runner, label, row["target"].As<string>(), owner, cancellationToken).ConfigureAwait(false);
            if (id is null) { unreachable++; continue; }
            reached.Add((relation.Name, id));
            if (exact || relation.Max == 1 && proposal.Links.Any(l => l.Relation == relation.Name)) continue;
            final[(relation.Name, id)] = new(relation.Name, id, label);
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
            if (targets.Count == 0 && exact && label != "Entity" && relation.To.Contains(':', StringComparison.Ordinal)) { leftOut++; continue; }
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
            unreachable != 0 || !reached.SetEquals(final.Keys), leftOut);
    }

    internal string TargetLabel(ModuleManifest module, ModuleRelation relation)
    {
        if (relation.To == "core:Entity") return "Entity";
        var parts = relation.To.Split(':');
        if (parts.Length == 1) return relation.To;
        var dependency = resolution.Modules.SingleOrDefault(m => m.Id == parts[0]);
        if (!module.Modules.ContainsKey(parts[0]) || dependency?.Ontology?.Types.All(t => t.Name != parts[1]) != false)
            throw new ModuleRecordRefusal("Link dependency is not bound to this store.");
        return parts[1];
    }

    private static Task<string?> FollowAsync(IAsyncQueryRunner runner, string label, string id,
        string? owner, CancellationToken cancellationToken) =>
        ModuleTargetResolver.FollowAsync(runner, label, id, owner, true, null, cancellationToken);
}
