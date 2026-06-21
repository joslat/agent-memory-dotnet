using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain.Schema;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Schema;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Neo4j-backed <see cref="ISchemaManager"/> (G4): persists custom entity-schema configurations as
/// versioned <c>:Schema</c> nodes, with at most one active version per name. The full
/// <see cref="EntitySchemaConfig"/> is JSON-serialized into the node's <c>config</c> property (via
/// <see cref="SchemaLoader"/>); <c>name</c>/<c>version</c>/<c>description</c> are mirrored as top-level
/// properties for indexed lookup. Schema nodes are global, not owner-scoped.
/// </summary>
public sealed class Neo4jSchemaManager : ISchemaManager
{
    private readonly INeo4jTransactionRunner _tx;
    private readonly IIdGenerator _idGenerator;
    private readonly IClock _clock;
    private readonly ILogger<Neo4jSchemaManager> _logger;

    /// <summary>Initializes a new instance of <see cref="Neo4jSchemaManager"/>.</summary>
    public Neo4jSchemaManager(
        INeo4jTransactionRunner tx,
        IIdGenerator idGenerator,
        IClock clock,
        ILogger<Neo4jSchemaManager> logger)
    {
        _tx = tx;
        _idGenerator = idGenerator;
        _clock = clock;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<EntitySchemaConfig?> LoadSchemaAsync(string name, CancellationToken ct = default)
    {
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(SchemaPersistenceQueries.LoadActiveByName, new { name }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count == 0 ? null : MapToConfig(records[0]["s"].As<INode>());
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<EntitySchemaConfig?> LoadSchemaVersionAsync(string name, string version, CancellationToken ct = default)
    {
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(SchemaPersistenceQueries.LoadByNameVersion, new { name, version }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count == 0 ? null : MapToConfig(records[0]["s"].As<INode>());
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveSchemaAsync(EntitySchemaConfig schema, string? createdBy = null, bool setActive = true, CancellationToken ct = default)
    {
        var id = _idGenerator.GenerateId();
        var createdAtUtc = _clock.UtcNow.ToString("O");
        var config = SchemaLoader.Serialize(schema);

        // One write transaction so the deactivate-then-activate stays atomic: the saved version is the only
        // active one for its name (when setActive), never a window with zero or two active versions.
        await _tx.WriteAsync(async runner =>
        {
            if (setActive)
                await runner.RunAsync(SchemaPersistenceQueries.DeactivateByName, new { name = schema.Name }).ConfigureAwait(false);

            await runner.RunAsync(SchemaPersistenceQueries.Save, new
            {
                id,
                name = schema.Name,
                version = schema.Version,
                description = (object?)schema.Description,
                config,
                isActive = setActive,
                createdAtUtc,
                createdBy = (object?)createdBy
            }).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);

        _logger.LogDebug("Saved schema {Name} v{Version} (active={Active})", schema.Name, schema.Version, setActive);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<SchemaListItem>> ListSchemasAsync(string? nameFilter = null, CancellationToken ct = default)
    {
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(SchemaPersistenceQueries.List, new { nameFilter = (object?)nameFilter }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return (IReadOnlyList<SchemaListItem>)records.Select(r => new SchemaListItem
            {
                Name = r["name"].As<string>(),
                LatestVersion = r["latestVersion"].As<string>(),
                Description = r["description"]?.As<string>(),
                VersionCount = r["versionCount"].As<int>(),
                IsActive = r["isActive"].As<bool>()
            }).ToList();
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> SchemaExistsAsync(string name, CancellationToken ct = default)
    {
        return await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(SchemaPersistenceQueries.Exists, new { name }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["exists"].As<bool>();
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<bool> DeleteSchemaAsync(string schemaId, CancellationToken ct = default)
    {
        return await _tx.WriteAsync(async runner =>
        {
            var cursor = await runner.RunAsync(SchemaPersistenceQueries.DeleteById, new { id = schemaId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Count > 0 && records[0]["deleted"].As<bool>();
        }, ct).ConfigureAwait(false);
    }

    private static EntitySchemaConfig MapToConfig(INode node)
        => SchemaLoader.Deserialize(node["config"].As<string>());
}
