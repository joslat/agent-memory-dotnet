using System.ComponentModel;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer.Tools;

namespace Neo4j.AgentMemory.McpServer.Resources;

/// <summary>
/// MCP resource that returns assembled memory context for a session.
/// </summary>
[McpServerResourceType]
public sealed class ContextResource
{
    [McpServerResource(UriTemplate = "memory://context/{session_id}", Name = "memory_context", MimeType = "application/json"),
     Description("Returns assembled memory context for a given session, including recent messages, relevant entities, facts, and preferences.")]
    public static async Task<string> GetContext(
        IMemoryContextAssembler contextAssembler,
        [Description("Session identifier")] string session_id,
        [Description("Query text to match relevant context")] string? query = null,
        [Description("Maximum number of recent messages to include")] int maxRecentMessages = 20,
        CancellationToken cancellationToken = default)
    {
        var request = new RecallRequest
        {
            SessionId = session_id,
            Query = query ?? "",
            Options = new RecallOptions { MaxRecentMessages = maxRecentMessages }
        };

        var context = await contextAssembler.AssembleContextAsync(request, cancellationToken);

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
