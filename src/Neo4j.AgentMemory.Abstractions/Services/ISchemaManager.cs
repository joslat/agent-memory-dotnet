#pragma warning disable CS1591
using Neo4j.AgentMemory.Abstractions.Domain.Schema;

namespace Neo4j.AgentMemory.Abstractions.Services;

/// <summary>
/// Manages persistence of entity schema configurations in Neo4j, with versioning support.
/// Mirrors Python's SchemaManager class.
/// </summary>
public interface ISchemaManager
{
    /// <summary>Loads the active schema version by name. Returns null if not found.</summary>
    Task<EntitySchemaConfig?> LoadSchemaAsync(string name, CancellationToken ct = default);

    /// <summary>Loads a specific version of a schema. Returns null if not found.</summary>
    Task<EntitySchemaConfig?> LoadSchemaVersionAsync(string name, string version, CancellationToken ct = default);

    /// <summary>
    /// Saves a schema to the store. Creates a new version if the name already exists.
    /// When <paramref name="setActive"/> is true, the saved version becomes the active one.
    /// </summary>
    Task SaveSchemaAsync(EntitySchemaConfig schema, string? createdBy = null, bool setActive = true, CancellationToken ct = default);

    /// <summary>Lists all schemas, optionally filtered by name prefix.</summary>
    Task<IReadOnlyList<SchemaListItem>> ListSchemasAsync(string? nameFilter = null, CancellationToken ct = default);

    /// <summary>Returns true if a schema with the given name exists.</summary>
    Task<bool> SchemaExistsAsync(string name, CancellationToken ct = default);

    /// <summary>Deletes a specific schema version by its unique ID. Returns true if deleted.</summary>
    Task<bool> DeleteSchemaAsync(string schemaId, CancellationToken ct = default);
}
