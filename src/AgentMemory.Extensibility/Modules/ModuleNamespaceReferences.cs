using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Data-only checks for static schema references using the manifest's naming and reserved-name rules.</summary>
[Experimental("AMEXT001")]
public static class ModuleNamespaceReferences
{
    /// <summary>Returns distinct, ordinally ordered manifest and namespace errors without database access.</summary>
    /// <remarks>Owned historical labels and relationship types need not appear in the current ontology.
    /// Schema-object names are global and must not be reserved. Optional property writes must use valid
    /// identifiers and may not target SDK-managed properties. This check does not parse statements,
    /// authorize operations, resolve cross-module collisions or establish existing database ownership.</remarks>
    public static IReadOnlyList<string> Validate(ModuleManifest manifest, IReadOnlyList<string> labels,
        IReadOnlyList<string> relationshipTypes, IReadOnlyList<string> schemaObjectNames,
        IReadOnlyList<string>? writtenProperties = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(labels);
        ArgumentNullException.ThrowIfNull(relationshipTypes);
        ArgumentNullException.ThrowIfNull(schemaObjectNames);
        return new ModuleManifestValidator().ValidateReferences(manifest, labels, relationshipTypes, schemaObjectNames, writtenProperties ?? []);
    }
}
