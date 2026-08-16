namespace AgentMemory.Abstractions.Repositories;

/// <summary>
/// Repository for schema and index management.
/// </summary>
/// <remarks>
/// <b>Nothing implements, registers or calls this.</b> A repo-wide search returns exactly one line —
/// this declaration. A host resolving it with <c>GetRequiredService&lt;ISchemaRepository&gt;()</c>
/// throws at startup, so the interface is not a seam, it is a name in the public surface that looks
/// like one. Schema work is done by <c>AgentMemory.Neo4j.Infrastructure.ISchemaBootstrapper</c>,
/// which is registered and is what the CLI's <c>schema-check</c> verb uses.
/// </remarks>
[Obsolete(
    "ISchemaRepository has no implementation and no registration; resolving it throws. Use " +
    "ISchemaBootstrapper (AgentMemory.Neo4j) for schema initialisation and validation. This " +
    "interface is a 2.0 removal candidate and cannot be removed sooner without breaking SemVer.")]
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
