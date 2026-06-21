using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer.Tools;

namespace AgentMemory.McpServer.Resources;

/// <summary>
/// MCP resource that lists entities in the knowledge graph.
/// </summary>
[McpServerResourceType]
public sealed class EntityListResource
{
    [McpServerResource(UriTemplate = "memory://entities", Name = "memory_entities", MimeType = "application/json"),
     Description("Returns a paginated list of entities in the knowledge graph.")]
    public static async Task<string> GetEntities(
        IGraphQueryService graphQueryService,
        [Description("Maximum number of entities to return")] int limit = 50,
        [Description("Number of entities to skip")] int offset = 0,
        [Description("Filter by entity type (e.g., PERSON, LOCATION)")] string? type = null,
        [Description("Owner/user identifier (optional). When set, returns only that owner's plus shared (un-owned) entities; null = all owners (unscoped/admin). Set it in multi-tenant deployments to prevent cross-owner reads (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        // Clamp pagination: a negative SKIP/LIMIT is a Neo4j error and a huge limit is a resource-exhaustion
        // vector. Clamping is friendlier than throwing for a list endpoint.
        limit = Math.Clamp(limit, 1, 1000);
        offset = Math.Max(0, offset);

        // R1: owner-scope the listing so a multi-tenant client can't read other owners' entities.
        // null userId ⇒ unscoped (admin/single-tenant), matching the rest of the MCP surface.
        var conditions = new List<string>();
        if (type is not null) conditions.Add("e.type = $type");
        var hasOwner = !string.IsNullOrEmpty(userId);
        if (hasOwner) conditions.Add("(e.owner_id = $ownerId OR e.owner_id IS NULL)");
        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        var query = $"""
            MATCH (e:Entity)
            {whereClause}
            WITH e
            ORDER BY e.created_at DESC
            SKIP $offset
            LIMIT $limit
            OPTIONAL MATCH (e)-[:SAME_AS]-(alias)
            RETURN e.id AS id, e.name AS name, e.type AS type, count(alias) AS aliasCount
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = (long)limit,
            ["offset"] = (long)offset,
            ["type"] = (object?)type
        };
        if (hasOwner) parameters["ownerId"] = userId;

        var results = await graphQueryService.QueryAsync(query, parameters, cancellationToken).ConfigureAwait(false);

        return ToolJsonContext.Serialize(new
        {
            entities = results.Select(r => new
            {
                id = r.TryGetValue("id", out var id) ? id?.ToString() : null,
                name = r.TryGetValue("name", out var n) ? n?.ToString() : null,
                type = r.TryGetValue("type", out var t) ? t?.ToString() : null,
                aliasCount = r.TryGetValue("aliasCount", out var ac) ? Convert.ToInt64(ac) : 0L
            }),
            limit,
            offset,
            typeFilter = type
        });
    }
}
