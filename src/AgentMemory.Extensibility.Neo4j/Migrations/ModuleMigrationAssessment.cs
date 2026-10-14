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

    internal static ModuleMigrationPlan Assess(ModuleMigrationCompilation compilation, ModuleDatabaseSnapshot snapshot)
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
        var keyObjects = snapshot.Objects.Where(o => o.Name == ModuleBookkeepingSchema.KeyConstraintName).ToArray();
        foreach (var obj in keyObjects)
            if (obj.EntityType != "NODE" || !obj.Labels.SequenceEqual([ModuleBookkeepingSchema.KeyLabel])
                || !obj.Properties.SequenceEqual(["id"])
                || (obj.IsConstraint ? obj.Kind != "UNIQUENESS" : obj.OwningConstraint != ModuleBookkeepingSchema.KeyConstraintName))
                problems.Add("Foreign or incompatible module key constraint: " + obj.Name);
        if (snapshot.Objects.Any(o => o.Name != ModuleBookkeepingSchema.KeyConstraintName && o.EntityType == "NODE"
            && o.Labels.SequenceEqual([ModuleBookkeepingSchema.KeyLabel]) && o.Properties.SequenceEqual(["id"])
            && o.Kind is "UNIQUENESS" or "NODE_KEY" or "RANGE"))
            problems.Add("Equivalent module key schema object has another name.");
        var pendingBookkeeping = keyObjects.Any(o => o.IsConstraint) ? Array.Empty<string>() : [ModuleBookkeepingSchema.KeyConstraintName];
        var duplicates = snapshot.Entries.GroupBy(e => (e.ModuleId, e.Key)).Where(g => g.Count() != 1).ToArray();
        foreach (var duplicate in duplicates) problems.Add("Duplicate module ledger identity: " + duplicate.Key);
        var entries = snapshot.Entries.GroupBy(e => (e.ModuleId, e.Key)).ToDictionary(g => g.Key, g => g.First());
        foreach (var claim in compilation.Claims)
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
        foreach (var migration in compilation.Steps)
        {
            var step = migration.Definition;
            entries.TryGetValue((step.ModuleId, step.Key), out var entry);
            var completed = 0;
            if (entry is not null)
            {
                try { completed = ModuleMigrationProgress.Completed(migration, entry); }
                catch (InvalidOperationException e) { problems.Add(e.Message); }
            }
            for (var index = 0; index < migration.Operations.Count; index++)
            {
                if (migration.Operations[index].Shape is not { } shape) continue;
                var actual = snapshot.Objects.Where(o => o.Name == shape.Name).ToArray();
                var matches = actual.Any(o => Matches(o, shape));
                if (actual.Any(o => !Matches(o, shape) && !IsBackingIndex(o, shape)))
                    problems.Add("Existing schema object differs from declared shape: " + shape.Name);
                if (snapshot.Objects.Any(o => o.Name != shape.Name && Conflicts(o, shape)))
                    problems.Add("Existing equivalent schema object has another name: " + shape.Name);
                if (actual.Any(o => o.State == "FAILED")) problems.Add("Index is failed: " + shape.Name);
                if ((entry?.State == "applied" || completed > index) && !matches)
                    problems.Add("Applied schema object is missing: " + shape.Name);
                if (entry is null && actual.Length != 0)
                    problems.Add("Schema object has no matching migration intent: " + shape.Name);
            }
            if (entry?.State == "applied") applied.Add(step);
            else pending.Add(step);
        }
        var keys = compilation.Steps.Select(s => (s.Definition.ModuleId, s.Definition.Key)).ToHashSet();
        return new ModuleMigrationPlan
        {
            PendingBookkeeping = Array.AsReadOnly(pendingBookkeeping), Pending = pending.AsReadOnly(), PendingNamespaces = pendingNamespaces.AsReadOnly(), Applied = applied.AsReadOnly(), Problems = problems.AsReadOnly(),
            UndeclaredAppliedKeys = snapshot.Entries.Where(e => e.ModuleId != ModuleBookkeepingSchema.NamespaceLedgerId
                && e.State == "applied" && !keys.Contains((e.ModuleId, e.Key)))
                .Select(e => e.ModuleId + "/" + e.Key).OrderBy(k => k, StringComparer.Ordinal).ToList().AsReadOnly()
        };
    }

    private static bool Matches(ModuleDatabaseObject actual, ModuleMigrationSchemaObject shape) =>
        actual.IsConstraint == shape.IsConstraint && SameSchema(actual, shape) && actual.Kind == shape.Kind
        && (shape.IsConstraint || actual.OwningConstraint is null);

    private static bool IsBackingIndex(ModuleDatabaseObject actual, ModuleMigrationSchemaObject shape) =>
        shape.IsConstraint && !actual.IsConstraint && actual.Kind == "RANGE"
        && actual.OwningConstraint == shape.Name && SameSchema(actual, shape);

    private static bool SameSchema(ModuleDatabaseObject actual, ModuleMigrationSchemaObject shape) =>
        actual.EntityType == shape.EntityType && actual.Labels.SequenceEqual(shape.Labels)
        && actual.Properties.SequenceEqual(shape.Properties);

    private static bool Conflicts(ModuleDatabaseObject actual, ModuleMigrationSchemaObject shape) =>
        SameSchema(actual, shape) && actual.Kind is "RANGE" or "UNIQUENESS" or "RELATIONSHIP_UNIQUENESS" or "NODE_KEY" or "RELATIONSHIP_KEY";
}
