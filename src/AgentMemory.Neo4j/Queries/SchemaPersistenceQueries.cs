namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher for persisting custom entity-schema configurations as versioned <c>:Schema</c>
/// nodes (G4). Backs <see cref="AgentMemory.Abstractions.Services.ISchemaManager"/>.
/// <para>Each (<c>name</c>, <c>version</c>) pair is one logical schema revision; the <c>config</c> property
/// holds the JSON-serialized <c>EntitySchemaConfig</c>. At most one version per name is active
/// (<c>is_active = true</c>) — the invariant is maintained by deactivating the name's versions before
/// activating the saved one. Schema nodes are global (NOT owner-scoped): they describe the ontology shared
/// across all users, like <c>:Extractor</c> and <c>:Tool</c>.</para>
/// </summary>
internal static class SchemaPersistenceQueries
{
    /// <summary>
    /// Upsert a schema revision by (name, version). <c>id</c>/<c>created_at</c>/<c>created_by</c> are set
    /// only on creation; <c>description</c>/<c>config</c>/<c>is_active</c> are refreshed on every save so a
    /// re-save of the same version is idempotent.
    /// </summary>
    public const string Save = @"
            MERGE (s:Schema {name: $name, version: $version})
            ON CREATE SET
                s.id         = $id,
                s.created_at = datetime($createdAtUtc),
                s.created_by = $createdBy
            SET
                s.description = $description,
                s.config      = $config,
                s.is_active   = $isActive
            RETURN s";

    /// <summary>Deactivate every version of a named schema (run before activating the saved one).</summary>
    public const string DeactivateByName = @"
            MATCH (s:Schema {name: $name})
            SET s.is_active = false";

    /// <summary>Load the active version of a named schema (newest active wins if more than one somehow exists).</summary>
    public const string LoadActiveByName = @"
            MATCH (s:Schema {name: $name, is_active: true})
            RETURN s
            ORDER BY s.created_at DESC
            LIMIT 1";

    /// <summary>Load a specific (name, version) schema revision.</summary>
    public const string LoadByNameVersion = @"
            MATCH (s:Schema {name: $name, version: $version})
            RETURN s
            ORDER BY s.created_at DESC
            LIMIT 1";

    /// <summary>
    /// List schemas grouped by name: the newest revision's version/description, the total revision count,
    /// and whether any revision is active. Optional <c>$nameFilter</c> (null ⇒ all) matches by name prefix.
    /// </summary>
    public const string List = @"
            MATCH (s:Schema)
            WHERE $nameFilter IS NULL OR s.name STARTS WITH $nameFilter
            WITH s ORDER BY s.created_at DESC
            WITH s.name AS name, collect(s) AS versions
            RETURN
                name AS name,
                head(versions).version AS latestVersion,
                head(versions).description AS description,
                size(versions) AS versionCount,
                any(v IN versions WHERE coalesce(v.is_active, false)) AS isActive
            ORDER BY name";

    /// <summary>True when at least one revision of a named schema exists.</summary>
    public const string Exists = @"
            MATCH (s:Schema {name: $name})
            RETURN count(s) > 0 AS exists";

    /// <summary>Delete a single schema revision by its unique id. Returns whether a node was removed.</summary>
    public const string DeleteById = @"
            MATCH (s:Schema {id: $id})
            DELETE s
            RETURN count(s) > 0 AS deleted";
}
