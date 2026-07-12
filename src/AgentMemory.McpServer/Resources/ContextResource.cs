using System.ComponentModel;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.McpServer.Tools;

namespace AgentMemory.McpServer.Resources;

/// <summary>
/// MCP resource that returns assembled memory context for a session.
/// </summary>
[McpServerResourceType]
internal sealed class ContextResource
{
    [McpServerResource(UriTemplate = "memory://context/{session_id}", Name = "memory_context", MimeType = "application/json"),
     Description("Returns assembled memory context for a given session, including recent messages, relevant entities, facts, and preferences.")]
    public static async Task<string> GetContext(
        IMemoryContextAssembler contextAssembler,
        [Description("Session identifier")] string session_id,
        [Description("Query text to match relevant context")] string? query = null,
        [Description("Maximum number of recent messages to include")] int maxRecentMessages = 20,
        [Description("Owner/user identifier (optional). When set, the recalled entities/facts/preferences are confined to that owner's plus shared (un-owned) memory; null = all owners (unscoped/admin). Set it in multi-tenant deployments to prevent cross-owner reads (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        // R1: scope the recall to the owner so a multi-tenant client can't read other owners' memory
        // content via the context resource. null userId ⇒ unscoped (admin/single-tenant).
        var request = new RecallRequest
        {
            SessionId = session_id,
            UserId = userId,
            Query = query ?? "",
            Options = new RecallOptions { MaxRecentMessages = maxRecentMessages }
        };

        var context = await contextAssembler.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false);

        return ToolJsonContext.Serialize(new
        {
            sessionId = session_id,
            recentMessageCount = context.RecentMessages.Items.Count,
            relevantMessageCount = context.RelevantMessages.Items.Count,
            entityCount = context.RelevantEntities.Items.Count,
            factCount = context.RelevantFacts.Items.Count,
            preferenceCount = context.RelevantPreferences.Items.Count,
            traceCount = context.SimilarTraces.Items.Count,
            recentMessages = context.RecentMessages.Items.Select(m => new
            {
                id = m.MessageId,
                role = m.Role,
                content = m.Content?.Length > 500 ? m.Content[..500] + "..." : m.Content,
                timestamp = m.TimestampUtc
            }),
            entities = context.RelevantEntities.Items.Select(e => new
            {
                id = e.EntityId,
                name = e.Name,
                type = e.Type
            }),
            facts = context.RelevantFacts.Items.Select(f => new
            {
                id = f.FactId,
                subject = f.Subject,
                predicate = f.Predicate,
                @object = f.Object
            }),
            preferences = context.RelevantPreferences.Items.Select(p => new
            {
                id = p.PreferenceId,
                preference = p.PreferenceText,
                category = p.Category
            }),
            graphRagContext = context.GraphRagContext,
            assembledAtUtc = context.AssembledAtUtc
        });
    }
}
