using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

/// <summary>A module-owned migration definition. A definition is not permission to execute DDL.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleMigrationStep
{
    /// <summary>Owning module's full identity.</summary>
    public required string ModuleId { get; init; }
    /// <summary>Stable identity within the module; generated shapes use derived/object-name.</summary>
    public required string Key { get; init; }
    /// <summary>Lowercase SHA-256 over UTF-8 statement bytes after line endings are normalized to LF.</summary>
    public required string Sha256 { get; init; }
    /// <summary>Statement text whose content is pinned by <see cref="Sha256"/>.</summary>
    public required string Statement { get; init; }
}
