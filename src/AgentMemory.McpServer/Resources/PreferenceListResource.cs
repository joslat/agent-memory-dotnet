using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer.Tools;

namespace AgentMemory.McpServer.Resources;

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
        [Description("Owner/user identifier (optional). When set, returns only that owner's plus shared (un-owned) preferences; null = all owners (unscoped/admin). Set it in multi-tenant deployments to prevent cross-owner reads of preference text (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        // R1: owner-scope the listing so a multi-tenant client can't read other owners' preferences
        // (the free-text 'context' is sensitive). null userId ⇒ unscoped (admin/single-tenant).
        var conditions = new List<string>();
        if (category is not null) conditions.Add("p.category = $category");
        var hasOwner = !string.IsNullOrEmpty(userId);
        if (hasOwner) conditions.Add("(p.owner_id = $ownerId OR p.owner_id IS NULL)");
        var whereClause = conditions.Count > 0 ? "WHERE " + string.Join(" AND ", conditions) : "";

        var query = $"""
            MATCH (p:Preference)
            {whereClause}
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
        if (hasOwner) parameters["ownerId"] = userId;

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
