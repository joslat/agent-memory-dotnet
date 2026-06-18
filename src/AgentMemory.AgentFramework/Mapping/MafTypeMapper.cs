using Microsoft.Extensions.AI;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.AgentFramework.Mapping;

/// <summary>
/// Maps between MAF/MEAI types and internal domain types.
/// </summary>
internal static class MafTypeMapper
{
    /// <summary>
    /// Converts a <see cref="ChatMessage"/> to an internal <see cref="Message"/>.
    /// </summary>
    public static Message ToInternalMessage(
        ChatMessage chatMessage,
        string sessionId,
        string conversationId,
        IClock clock,
        IIdGenerator idGen)
    {
        return new Message
        {
            MessageId = idGen.GenerateId(),
            ConversationId = conversationId,
            SessionId = sessionId,
            Role = ToInternalRole(chatMessage.Role),
            Content = chatMessage.Text ?? string.Empty,
            TimestampUtc = clock.UtcNow,
            Metadata = new Dictionary<string, object>()
        };
    }

    /// <summary>
    /// Converts an internal <see cref="Message"/> to a <see cref="ChatMessage"/>.
    /// </summary>
    public static ChatMessage ToChatMessage(Message message)
        => new(ToMafRole(message.Role), message.Content);

    /// <summary>
    /// Converts a <see cref="MemoryContext"/> to a list of context <see cref="ChatMessage"/> instances.
    /// </summary>
    public static IReadOnlyList<ChatMessage> ToContextMessages(
        MemoryContext context,
        ContextFormatOptions? formatOptions = null)
    {
        var options = formatOptions ?? new ContextFormatOptions();

        // Build chat messages and memory-derived system messages into SEPARATE buckets and budget them
        // independently. The whole point of this provider is to inject long-term memory; appending memory
        // AFTER chat and then Take(MaxContextMessages) put memory at the tail, so once a conversation had
        // ~MaxContextMessages chat messages the memory blocks were the first dropped — silently injecting
        // ZERO long-term memory. Memory items are now always kept; only the chat portion is truncated.

        // Lead (always kept): optional prefix + graph context when it leads (GraphRagOnly/GraphRagThenMemory).
        var lead = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(options.ContextPrefix))
            lead.Add(new ChatMessage(ChatRole.System, options.ContextPrefix));

        bool graphFirst = context.BlendMode is RetrievalBlendMode.GraphRagOnly or RetrievalBlendMode.GraphRagThenMemory;
        if (graphFirst && !string.IsNullOrEmpty(context.GraphRagContext))
            lead.Add(new ChatMessage(ChatRole.System, context.GraphRagContext));

        // Chat (truncatable): dedup across RecentMessages and RelevantMessages — a message may appear in
        // both. DistinctBy preserves insertion order (recent-first) while dropping subsequent duplicates.
        var chatMessages = context.RecentMessages.Items
            .Concat(context.RelevantMessages.Items)
            .DistinctBy(m => m.MessageId)
            .Select(ToChatMessage)
            .ToList();

        // Memory-derived system messages (always kept).
        var memory = new List<ChatMessage>();
        if (options.IncludeEntities && context.RelevantEntities.Items.Count > 0)
        {
            var entityText = string.Join(", ", context.RelevantEntities.Items
                .Select(e => string.IsNullOrEmpty(e.Description)
                    ? $"{e.Name} ({e.Type})"
                    : $"{e.Name} ({e.Type}): {e.Description}"));
            memory.Add(new ChatMessage(ChatRole.System, $"Relevant entities: {entityText}"));
        }

        if (options.IncludeFacts && context.RelevantFacts.Items.Count > 0)
        {
            var factText = string.Join("; ", context.RelevantFacts.Items
                .Select(f => $"{f.Subject} {f.Predicate} {f.Object}"));
            memory.Add(new ChatMessage(ChatRole.System, $"Known facts: {factText}"));
        }

        if (options.IncludePreferences && context.RelevantPreferences.Items.Count > 0)
        {
            var prefText = string.Join("; ", context.RelevantPreferences.Items
                .Select(p => p.PreferenceText));
            memory.Add(new ChatMessage(ChatRole.System, $"User preferences: {prefText}"));
        }

        if (options.IncludeReasoningTraces && context.SimilarTraces.Items.Count > 0)
        {
            var traceText = string.Join("; ", context.SimilarTraces.Items
                .Select(t => t.Task));
            memory.Add(new ChatMessage(ChatRole.System, $"Similar past tasks: {traceText}"));
        }

        if (!graphFirst && !string.IsNullOrEmpty(context.GraphRagContext))
            memory.Add(new ChatMessage(ChatRole.System, context.GraphRagContext));

        // Fill the budget left over after the always-kept lead + memory with the MOST RECENT chat
        // messages. Order is preserved (lead → chat → memory), matching the original layout.
        int chatBudget = Math.Max(0, options.MaxContextMessages - lead.Count - memory.Count);
        var keptChat = chatMessages.Count > chatBudget
            ? chatMessages.Skip(chatMessages.Count - chatBudget).ToList()
            : chatMessages;

        var result = new List<ChatMessage>(lead.Count + keptChat.Count + memory.Count);
        result.AddRange(lead);
        result.AddRange(keptChat);
        result.AddRange(memory);
        return result;
    }

    internal static string ToInternalRole(ChatRole role)
    {
        if (role == ChatRole.User) return "user";
        if (role == ChatRole.Assistant) return "assistant";
        if (role == ChatRole.System) return "system";
        if (role == ChatRole.Tool) return "tool";
        return role.Value ?? "user";
    }

    internal static ChatRole ToMafRole(string role) => role switch
    {
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        "system" => ChatRole.System,
        "tool" => ChatRole.Tool,
        _ => new ChatRole(role)
    };
}
