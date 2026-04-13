using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.McpServer.Tools;

/// <summary>
/// Conversation tools: get conversation history, list sessions.
/// </summary>
[McpServerToolType]
public sealed class ConversationTools
{
    [McpServerTool(Name = "memory_get_conversation"), Description("Get the message history for a specific conversation.")]
    public static async Task<string> MemoryGetConversation(
        IShortTermMemoryService shortTermMemory,
        [Description("The conversation identifier to retrieve messages for")] string conversationId,
        CancellationToken cancellationToken = default)
    {
        var messages = await shortTermMemory.GetConversationMessagesAsync(conversationId, cancellationToken);
        return ToolJsonContext.Serialize(messages.Select(m => new
        {
            m.MessageId,
            m.ConversationId,
            m.SessionId,
            m.Role,
            m.Content,
            m.TimestampUtc
        }));
    }

    [McpServerTool(Name = "memory_list_sessions"), Description("List conversations for a given session.")]
    public static async Task<string> MemoryListSessions(
        IConversationRepository conversationRepo,
        IOptions<McpServerOptions> options,
        [Description("Session identifier to list conversations for (optional, uses default if omitted)")] string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var sid = sessionId ?? options.Value.DefaultSessionId;
        var conversations = await conversationRepo.GetBySessionAsync(sid, cancellationToken);
        return ToolJsonContext.Serialize(conversations.Select(c => new
        {
            c.ConversationId,
            c.SessionId,
            c.UserId,
            c.CreatedAtUtc,
            c.UpdatedAtUtc
        }));
    }
}
