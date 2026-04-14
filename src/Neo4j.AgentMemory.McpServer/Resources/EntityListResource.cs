using System.ComponentModel;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer.Resources;

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
        CancellationToken cancellationToken = default)
    {
        var typeFilter = type is not null
            ? "WHERE e.type = $type"
            : "";

        var query = $"""
            MATCH (e:Entity)
            {typeFilter}
            WITH e
            ORDER BY e.createdAtUtc DESC
            SKIP $offset
            LIMIT $limit
            OPTIONAL MATCH (e)-[:SAME_AS]-(alias)
            RETURN e.entityId AS id, e.name AS name, e.type AS type, count(alias) AS aliasCount
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = (long)limit,
            ["offset"] = (long)offset,
            ["type"] = (object?)type
        };

        var results = await graphQueryService.QueryAsync(query, parameters, cancellationToken);

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
