using System.Diagnostics.CodeAnalysis;
using System.Text;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

[Experimental("AMEXT001")]
internal sealed record ModuleCompiledMigration
{
    internal required ModuleMigrationStep Definition { get; init; }
    internal IReadOnlyList<ModuleMigrationOperation> Operations { get; init; } = [];
}

[Experimental("AMEXT001")]
internal sealed record ModuleMigrationCompilation
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal IReadOnlyList<ModuleCompiledMigration> Steps { get; init; } = [];
    internal IReadOnlyList<ModuleNamespaceClaim> Claims { get; init; } = [];

    internal static ModuleMigrationCompilation Compile(ModuleResolution resolution, IReadOnlyList<ModuleMigrationStep> scripts)
    {
        ArgumentNullException.ThrowIfNull(scripts);
        var generated = ModuleMigrationDefinitions.Derive(resolution);
        var modules = resolution.Modules.ToDictionary(m => m.Id, StringComparer.Ordinal);
        var supplied = new Dictionary<(string, string), ModuleMigrationStep>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var sizes = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var step in scripts)
        {
            if (step is null || step.ModuleId is null || !modules.ContainsKey(step.ModuleId)) throw Invalid("script names an unresolved module");
            if (!ModuleMigrationScripts.IsValidKey(step.Key)) throw Invalid("script key must be 000N_name");
            if (!supplied.TryAdd((step.ModuleId, step.Key), step)) throw Invalid("duplicate script key: " + step.Key);
            if (step.Statement is null || step.Statement.StartsWith('\uFEFF')) throw Invalid("missing source or unexpected UTF-8 BOM");
            int bytes;
            try { bytes = StrictUtf8.GetByteCount(step.Statement); }
            catch (EncoderFallbackException e) { throw new FormatException("Module migration source contains invalid Unicode.", e); }
            var count = counts.GetValueOrDefault(step.ModuleId) + 1;
            var total = sizes.GetValueOrDefault(step.ModuleId) + bytes;
            if (bytes > ModuleMigrationScripts.MaximumScriptBytes || total > ModuleMigrationScripts.MaximumTotalBytes
                || count > ModuleMigrationScripts.MaximumScriptCount) throw Invalid("module script size/count limit exceeded");
            counts[step.ModuleId] = count;
            sizes[step.ModuleId] = total;
            if (step.Sha256 != ModuleMigrationDefinitions.Hash(step.Statement)) throw Invalid("script hash does not match full normalized source: " + step.Key);
        }

        var compiled = new List<ModuleCompiledMigration>();
        var claims = new Dictionary<string, ModuleNamespaceClaim>(StringComparer.Ordinal);
        var shapes = new List<ModuleMigrationSchemaObject>();
        foreach (var claim in ModuleMigrationAssessment.Claims(resolution)) Claim(claim);
        foreach (var module in resolution.Modules)
        {
            var definitions = generated.Where(step => step.ModuleId == module.Id)
                .Concat(supplied.Values.Where(step => step.ModuleId == module.Id).OrderBy(step => step.Key, StringComparer.Ordinal));
            foreach (var definition in definitions)
            {
                var statements = ModuleCypherScript.Split(definition.Statement);
                if (statements.Count == 0) throw Invalid("empty migration: " + definition.Key);
                var operations = statements.Select(source => ModuleMigrationOperations.Compile(module, source)).ToList().AsReadOnly();
                foreach (var operation in operations)
                {
                    foreach (var label in operation.Labels) Claim(new("namespace/label/" + label, module.Id, "label", label));
                    foreach (var relation in operation.RelationshipTypes) Claim(new("namespace/relation/" + relation, module.Id, "relation", relation));
                    if (operation.Shape is { } shape)
                    {
                        if (shapes.Any(s => s.Name == shape.Name)) throw Invalid("duplicate schema object: " + shape.Name);
                        if (shapes.Any(s => SameSchema(s, shape))) throw Invalid("equivalent schema object has another name: " + shape.Name);
                        shapes.Add(shape);
                        Claim(new("namespace/object/" + shape.Name, module.Id, "object", shape.Name));
                    }
                }
                compiled.Add(new() { Definition = definition, Operations = operations });
            }
        }
        return new() { Steps = compiled.AsReadOnly(), Claims = claims.Values.OrderBy(c => c.Key, StringComparer.Ordinal).ToList().AsReadOnly() };

        void Claim(ModuleNamespaceClaim claim)
        {
            if (claims.TryGetValue(claim.Key, out var existing) && existing.Owner != claim.Owner)
                throw Invalid("conflicting namespace ownership: " + claim.Name);
            claims[claim.Key] = claim;
        }
    }

    internal static bool SameSchema(ModuleMigrationSchemaObject a, ModuleMigrationSchemaObject b) =>
        a.EntityType == b.EntityType && a.Labels.SequenceEqual(b.Labels) && a.Properties.SequenceEqual(b.Properties);

    private static FormatException Invalid(string message) => new("Module migration compilation: " + message + ".");
}
