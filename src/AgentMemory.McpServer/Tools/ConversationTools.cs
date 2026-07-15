using System.ComponentModel;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.McpServer.Tools;

/// <summary>
/// Conversation tools: get conversation history, list sessions.
/// </summary>
[McpServerToolType]
internal sealed class ConversationTools
{
    [McpServerTool(Name = "memory_get_conversation"), Description("Get the message history for a specific conversation.")]
    public static async Task<string> MemoryGetConversation(
        IShortTermMemoryService shortTermMemory,
        IConversationRepository conversationRepo,
        IMemoryIsolationPolicy isolationPolicy,
        [Description("The conversation identifier to retrieve messages for")] string conversationId,
        [Description("Owner/user identifier (optional). When set, returns messages only if the conversation belongs to that user or is un-attributed; otherwise an empty array. null = unscoped/admin. Set it in multi-tenant deployments to prevent cross-owner reads (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        // #100 Stage 2: fails closed under StrictMultiTenant before any repository call, same gate as
        // every other tenant-facing tool. IConversationRepository has no MemoryScope-aware query method
        // (unlike IEntityRepository/IFactRepository/etc.), so the actual owner check below remains the
        // existing post-hoc, in-memory filter -- real Cypher-level scoping for conversations is a bigger,
        // separate change (would need new IConversationRepository query methods), not this fix's job.
        var resolvedUserId = isolationPolicy.ResolveReadScope(
            explicitScope: null, userId, nameof(MemoryGetConversation), MemoryOperationAccess.Tenant).OwnerId;

        // R1: a conversationId is not a private random handle (it defaults to the guessable session id and
        // is enumerable via memory://conversations), so a scoped caller must not read another owner's
        // messages. Conversations carry user_id; deny when the conversation is owned by someone else.
        if (!string.IsNullOrEmpty(resolvedUserId))
        {
            var conversation = await conversationRepo.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false);
            if (conversation is null ||
                (conversation.UserId is not null && conversation.UserId != resolvedUserId))
            {
                return ToolJsonContext.Serialize(Array.Empty<object>());
            }
        }

        var messages = await shortTermMemory.GetConversationMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);
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
        IOptions<AgentMemoryMcpOptions> options,
        IMemoryIsolationPolicy isolationPolicy,
        [Description("Session identifier to list conversations for (optional, uses default if omitted)")] string? sessionId = null,
        [Description("Owner/user identifier (optional). When set, lists only that user's (plus un-attributed) conversations; null = all users (unscoped/admin). Set it in multi-tenant deployments to avoid leaking other users' session ids (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        // #100 Stage 2: same gate + same caveat as MemoryGetConversation above.
        var resolvedUserId = isolationPolicy.ResolveReadScope(
            explicitScope: null, userId, nameof(MemoryListSessions), MemoryOperationAccess.Tenant).OwnerId;

        var sid = sessionId ?? options.Value.DefaultSessionId;
        var conversations = await conversationRepo.GetBySessionAsync(sid, cancellationToken).ConfigureAwait(false);

        // R1: a session id is shareable/guessable, so a scoped caller sees only their own (or un-attributed)
        // conversations — never another owner's. null userId ⇒ unscoped (admin/single-tenant).
        if (!string.IsNullOrEmpty(resolvedUserId))
            conversations = conversations.Where(c => c.UserId is null || c.UserId == resolvedUserId).ToList();

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
