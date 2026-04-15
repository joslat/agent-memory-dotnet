using System.ComponentModel;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer.Resources;

/// <summary>
/// MCP resource that lists preferences grouped by category.
/// </summary>
[McpServerResourceType]
public sealed class PreferenceListResource
{
    [McpServerResource(UriTemplate = "memory://preferences", Name = "memory_preferences", MimeType = "application/json"),
     Description("Returns preferences grouped by category.")]
    public static async Task<string> GetPreferences(
        IGraphQueryService graphQueryService,
        [Description("Filter by category (optional)")] string? category = null,
        [Description("Maximum number of preferences to return")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var categoryFilter = category is not null
            ? "WHERE p.category = $category"
            : "";

        var query = $"""
            MATCH (p:Preference)
            {categoryFilter}
            WITH p
            ORDER BY p.category, p.created_at DESC
            LIMIT $limit
            RETURN p.id AS id, p.preference AS preference, p.category AS category,
                   p.context AS context, p.confidence AS confidence,
                   p.created_at AS createdAt
            """;

        var parameters = new Dictionary<string, object?>
        {
            ["limit"] = (long)limit,
            ["category"] = (object?)category
        };

        var results = await graphQueryService.QueryAsync(query, parameters, cancellationToken);

        return ToolJsonContext.Serialize(new
        {
            preferences = results.Select(r => new
            {
                id = r.TryGetValue("id", out var id) ? id?.ToString() : null,
                preference = r.TryGetValue("preference", out var pref) ? pref?.ToString() : null,
                category = r.TryGetValue("category", out var cat) ? cat?.ToString() : null,
                context = r.TryGetValue("context", out var ctx) ? ctx?.ToString() : null,
                confidence = r.TryGetValue("confidence", out var conf) ? Convert.ToDouble(conf) : 0.0,
                createdAt = r.TryGetValue("createdAt", out var ca) ? ca?.ToString() : null
            }),
            limit,
            categoryFilter = category
        });
    }
}
