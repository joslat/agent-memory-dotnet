using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Shared;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

internal sealed record ModuleNamespaceClaim(string Key, string Owner, string Kind, string Name);

[Experimental("AMEXT001")]
internal static class ModuleMigrationAssessment
{
    // This public storage contract is pinned against the actual bootstrap declarations by tests.
    internal static readonly string[] BaseConstraints = ["conversation_id", "message_id", "entity_id",
        "fact_id", "preference_id", "reasoning_trace_id", "reasoning_step_id", "tool_call_id",
        "tool_name", "extractor_name", "consolidation_run_id", "memory_read_audit_id"];

    internal static IReadOnlyList<ModuleNamespaceClaim> Claims(ModuleResolution resolution) =>
        resolution.Modules.SelectMany(m =>
            (m.Ontology is null ? [] : new[] { new ModuleNamespaceClaim("namespace/prefix/" + m.Ontology.LabelPrefix, m.Id, "prefix", m.Ontology.LabelPrefix) })
            .Concat((m.Ontology?.Types ?? []).Select(t => new ModuleNamespaceClaim("namespace/label/" + t.Name, m.Id, "label", t.Name)))
            .Concat((m.Ontology?.Relations ?? []).Select(r => new ModuleNamespaceClaim("namespace/relation/" + r.Name, m.Id, "relation", r.Name)))
            .Concat(ModuleSchemaObjects.Describe(m).Select(s => new ModuleNamespaceClaim("namespace/object/" + s.Name, m.Id, "object", s.Name))))
        .OrderBy(c => c.Key, StringComparer.Ordinal).ToList().AsReadOnly();

    internal static ModuleMigrationPlan Assess(ModuleResolution resolution, IReadOnlyList<ModuleMigrationStep> steps,
        IReadOnlyList<ModuleNamespaceClaim> claims, ModuleDatabaseSnapshot snapshot)
    {
        var problems = new List<string>();
        var pending = new List<ModuleMigrationStep>();
        var pendingNamespaces = new List<string>();
        var applied = new List<ModuleMigrationStep>();
        var missing = BaseConstraints.Except(snapshot.Objects.Where(o => o.IsConstraint).Select(o => o.Name), StringComparer.Ordinal).ToArray();
        if (missing.Length != 0) problems.Add("Bootstrap the base schema first; missing constraints: " + string.Join(", ", missing));
        foreach (var obj in snapshot.Objects.Where(o => o.Name == ModuleBookkeepingSchema.ConstraintName))
        {
            if (obj.EntityType != "NODE" || !obj.Labels.SequenceEqual([ModuleBookkeepingSchema.Label])
                || !obj.Properties.SequenceEqual(["module_id", "key"])
                || (obj.IsConstraint ? obj.Kind != "UNIQUENESS" : obj.OwningConstraint != ModuleBookkeepingSchema.ConstraintName))
                problems.Add("Foreign or incompatible module ledger constraint: " + obj.Name);
        }
        var duplicates = snapshot.Entries.GroupBy(e => (e.ModuleId, e.Key)).Where(g => g.Count() != 1).ToArray();
        foreach (var duplicate in duplicates) problems.Add("Duplicate module ledger identity: " + duplicate.Key);
        var entries = snapshot.Entries.GroupBy(e => (e.ModuleId, e.Key)).ToDictionary(g => g.Key, g => g.First());
        foreach (var claim in claims)
        {
            var exists = claim.Kind switch
            {
                "label" => snapshot.Labels.Contains(claim.Name) || snapshot.Objects.Any(o => o.EntityType == "NODE" && o.Labels.Contains(claim.Name)),
                "relation" => snapshot.Relations.Contains(claim.Name) || snapshot.Objects.Any(o => o.EntityType == "RELATIONSHIP" && o.Labels.Contains(claim.Name)),
                _ => snapshot.Objects.Any(o => o.Name == claim.Name)
            };
            if (entries.TryGetValue((ModuleBookkeepingSchema.NamespaceLedgerId, claim.Key), out var row))
            {
                if (row.Owner != claim.Owner || row.State != "owned") problems.Add("Namespace is owned by another module or has invalid ownership: " + claim.Name);
            }
            else
            {
                pendingNamespaces.Add(claim.Key);
                if (exists) problems.Add("Existing namespace has no matching module ownership: " + claim.Name);
            }
        }
        var shapes = resolution.Modules.SelectMany(ModuleSchemaObjects.Describe).ToDictionary(s => s.Name, StringComparer.Ordinal);
        foreach (var step in steps)
        {
            var shape = shapes[step.Key["derived/".Length..]];
            var actual = snapshot.Objects.Where(o => o.Name == shape.Name).ToArray();
            var constraint = shape.Kind == ModuleSchemaObjectKind.UniqueConstraint;
            var matches = actual.Any(o => Matches(o, shape, constraint));
            if (actual.Any(o => !Matches(o, shape, constraint)
                && !(constraint && !o.IsConstraint && o.OwningConstraint == shape.Name
                    && o.EntityType == "NODE" && o.Labels.SequenceEqual([shape.Label]) && o.Properties.SequenceEqual([shape.Property]))))
                problems.Add("Existing schema object differs from declared shape: " + shape.Name);
            if (actual.Any(o => o.State == "FAILED")) problems.Add("Index is failed: " + shape.Name);
            if (entries.TryGetValue((step.ModuleId, step.Key), out var entry))
            {
                if (entry.Hash != step.Sha256) problems.Add("Migration hash differs from reserved/applied hash: " + step.ModuleId + "/" + step.Key);
                if (entry.State is not ("applying" or "applied")) problems.Add("Invalid migration state: " + step.ModuleId + "/" + step.Key);
                if (entry.State == "applied")
                {
                    if (!matches) problems.Add("Applied schema object is missing: " + shape.Name);
                    applied.Add(step);
                }
                else pending.Add(step);
            }
            else
            {
                if (actual.Length != 0) problems.Add("Schema object has no matching migration intent: " + shape.Name);
                pending.Add(step);
            }
        }
        var keys = steps.Select(s => (s.ModuleId, s.Key)).ToHashSet();
        return new ModuleMigrationPlan
        {
            Pending = pending.AsReadOnly(), PendingNamespaces = pendingNamespaces.AsReadOnly(), Applied = applied.AsReadOnly(), Problems = problems.AsReadOnly(),
            UndeclaredAppliedKeys = snapshot.Entries.Where(e => e.ModuleId != ModuleBookkeepingSchema.NamespaceLedgerId
                && e.State == "applied" && !keys.Contains((e.ModuleId, e.Key)))
                .Select(e => e.ModuleId + "/" + e.Key).OrderBy(k => k, StringComparer.Ordinal).ToList().AsReadOnly()
        };
    }

    private static bool Matches(ModuleDatabaseObject o, ModuleSchemaObject shape, bool constraint) =>
        o.IsConstraint == constraint && o.EntityType == "NODE" && o.Labels.SequenceEqual([shape.Label])
        && o.Properties.SequenceEqual([shape.Property]) && o.Kind == (constraint ? "UNIQUENESS" : "RANGE")
        && (constraint || o.OwningConstraint is null);
}
