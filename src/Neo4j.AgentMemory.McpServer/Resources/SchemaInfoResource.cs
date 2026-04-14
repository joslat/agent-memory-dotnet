using System.ComponentModel;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer.Resources;

/// <summary>
/// MCP resource that exposes the current graph schema.
/// </summary>
[McpServerResourceType]
public sealed class SchemaInfoResource
{
    [McpServerResource(UriTemplate = "memory://schema", Name = "memory_schema", MimeType = "application/json"),
     Description("Returns the current graph schema including node labels, relationship types, and property keys.")]
    public static async Task<string> GetSchema(
        IGraphQueryService graphQueryService,
        CancellationToken cancellationToken = default)
    {
        var labelsQuery = "CALL db.labels() YIELD label RETURN collect(label) AS labels";
        var relTypesQuery = "CALL db.relationshipTypes() YIELD relationshipType RETURN collect(relationshipType) AS relationshipTypes";
        var propKeysQuery = "CALL db.propertyKeys() YIELD propertyKey RETURN collect(propertyKey) AS propertyKeys";

        var labelsResult = await graphQueryService.QueryAsync(labelsQuery, cancellationToken: cancellationToken);
        var relTypesResult = await graphQueryService.QueryAsync(relTypesQuery, cancellationToken: cancellationToken);
        var propKeysResult = await graphQueryService.QueryAsync(propKeysQuery, cancellationToken: cancellationToken);

        var labels = labelsResult.FirstOrDefault()?.TryGetValue("labels", out var l) == true
            ? l as IEnumerable<object> ?? Array.Empty<object>()
            : Array.Empty<object>();

        var relTypes = relTypesResult.FirstOrDefault()?.TryGetValue("relationshipTypes", out var r) == true
            ? r as IEnumerable<object> ?? Array.Empty<object>()
            : Array.Empty<object>();

        var propKeys = propKeysResult.FirstOrDefault()?.TryGetValue("propertyKeys", out var p) == true
            ? p as IEnumerable<object> ?? Array.Empty<object>()
            : Array.Empty<object>();

        return ToolJsonContext.Serialize(new
        {
            labels = labels.Select(x => x?.ToString()),
            relationshipTypes = relTypes.Select(x => x?.ToString()),
            propertyKeys = propKeys.Select(x => x?.ToString()),
            retrievedAtUtc = DateTimeOffset.UtcNow
        });
    }
}
