using System.Collections.Frozen;
using AgentMemory.Shared;

namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>Declares ownership of the optional SDK ledger without running any core migration.</summary>
internal sealed class ModuleBookkeepingSchemaExtension : ISchemaExtension
{
    public string Id => ModuleBookkeepingSchema.ExtensionId;
    public int Version => 2;
    public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        { [ModuleBookkeepingSchema.Label] = ModuleBookkeepingSchema.Properties,
          [ModuleBookkeepingSchema.KeyLabel] = ModuleBookkeepingSchema.KeyProperties }.ToFrozenDictionary(StringComparer.Ordinal);
    public IReadOnlySet<string> DeclaredLabels { get; } =
        new[] { ModuleBookkeepingSchema.Label, ModuleBookkeepingSchema.KeyLabel }.ToFrozenSet(StringComparer.Ordinal);
    public IReadOnlySet<string> DeclaredRelationshipTypes { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    public IReadOnlyList<string> MigrationScripts { get; } = [];
    public IReadOnlySet<string> BaseResidentMigrations { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    public SchemaParityDelta ParityDelta { get; } = SchemaParityDelta.Create(
        addNetOnlyLabels: [ModuleBookkeepingSchema.Label, ModuleBookkeepingSchema.KeyLabel],
        addNetSupersetProperties: ModuleBookkeepingSchema.Properties.Concat(ModuleBookkeepingSchema.KeyProperties));
    public IReadOnlySet<string> DependsOn { get; } = Array.Empty<string>().ToFrozenSet(StringComparer.Ordinal);
    public TckProfileDescriptor TckProfile => TckProfileDescriptor.None;
}
