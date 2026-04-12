namespace Neo4j.AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for schema and index management.
/// </summary>
public interface ISchemaRepository
{
    /// <summary>
    /// Initializes the database schema (constraints, indexes).
    /// </summary>
    Task InitializeSchemaAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if the schema is initialized.
    /// </summary>
    Task<bool> IsSchemaInitializedAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current schema version.
    /// </summary>
    Task<int?> GetSchemaVersionAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies a schema migration to a target version.
    /// </summary>
    Task ApplyMigrationAsync(
        int targetVersion,
        CancellationToken cancellationToken = default);
}
