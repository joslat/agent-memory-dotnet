using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

/// <summary>Pure local DDL preview for editors and tools, using the migrator's derivation logic.</summary>
[Experimental("AMEXT001")]
public static class ModuleSchemaDerivation
{
    /// <summary>Validates local manifest metadata and returns ordered generated definitions and hashes.</summary>
    /// <remarks>No database access, dependency loading, module execution or migration application occurs.
    /// This preview does not validate the complete composition, host capabilities, source trust or existing
    /// database ownership. Those checks remain mandatory before application. Handwritten scripts are not
    /// part of generated-schema preview. Input collections must not be mutated during this call.</remarks>
    public static IReadOnlyList<ModuleMigrationStep> Derive(ModuleManifest manifest) =>
        ModuleMigrationDefinitions.DeriveLocal(manifest);
}
