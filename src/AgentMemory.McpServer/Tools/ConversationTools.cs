using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// Conversation tools: get conversation history, list sessions.
/// </summary>
[McpServerToolType]
public sealed class ConversationTools
{
    [McpServerTool(Name = "memory_get_conversation"), Description("Get the message history for a specific conversation.")]
    public static async Task<string> MemoryGetConversation(
        IShortTermMemoryService shortTermMemory,
        IConversationRepository conversationRepo,
        [Description("The conversation identifier to retrieve messages for")] string conversationId,
        [Description("Owner/user identifier (optional). When set, returns messages only if the conversation belongs to that user or is un-attributed; otherwise an empty array. null = unscoped/admin. Set it in multi-tenant deployments to prevent cross-owner reads (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        // R1: a conversationId is not a private random handle (it defaults to the guessable session id and
        // is enumerable via memory://conversations), so a scoped caller must not read another owner's
        // messages. Conversations carry user_id; deny when the conversation is owned by someone else.
        if (!string.IsNullOrEmpty(userId))
        {
            var conversation = await conversationRepo.GetByIdAsync(conversationId, cancellationToken);
            if (conversation is null ||
                (conversation.UserId is not null && conversation.UserId != userId))
            {
                return ToolJsonContext.Serialize(Array.Empty<object>());
            }
        }

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
        [Description("Owner/user identifier (optional). When set, lists only that user's (plus un-attributed) conversations; null = all users (unscoped/admin). Set it in multi-tenant deployments to avoid leaking other users' session ids (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var sid = sessionId ?? options.Value.DefaultSessionId;
        var conversations = await conversationRepo.GetBySessionAsync(sid, cancellationToken);

        // R1: a session id is shareable/guessable, so a scoped caller sees only their own (or un-attributed)
        // conversations — never another owner's. null userId ⇒ unscoped (admin/single-tenant).
        if (!string.IsNullOrEmpty(userId))
            conversations = conversations.Where(c => c.UserId is null || c.UserId == userId).ToList();

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
