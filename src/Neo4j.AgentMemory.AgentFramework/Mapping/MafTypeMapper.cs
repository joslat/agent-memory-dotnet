using Microsoft.Extensions.AI;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.AgentFramework.Mapping;

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
        var messages = new List<ChatMessage>();

        if (!string.IsNullOrWhiteSpace(options.ContextPrefix))
            messages.Add(new ChatMessage(ChatRole.System, options.ContextPrefix));

        foreach (var m in context.RecentMessages.Items)
            messages.Add(ToChatMessage(m));

        foreach (var m in context.RelevantMessages.Items)
            messages.Add(ToChatMessage(m));

        if (options.IncludeEntities && context.RelevantEntities.Items.Count > 0)
        {
            var entityText = string.Join(", ", context.RelevantEntities.Items
                .Select(e => string.IsNullOrEmpty(e.Description)
                    ? $"{e.Name} ({e.Type})"
                    : $"{e.Name} ({e.Type}): {e.Description}"));
            messages.Add(new ChatMessage(ChatRole.System, $"Relevant entities: {entityText}"));
        }

        if (options.IncludeFacts && context.RelevantFacts.Items.Count > 0)
        {
            var factText = string.Join("; ", context.RelevantFacts.Items
                .Select(f => $"{f.Subject} {f.Predicate} {f.Object}"));
            messages.Add(new ChatMessage(ChatRole.System, $"Known facts: {factText}"));
        }

        if (options.IncludePreferences && context.RelevantPreferences.Items.Count > 0)
        {
            var prefText = string.Join("; ", context.RelevantPreferences.Items
                .Select(p => p.PreferenceText));
            messages.Add(new ChatMessage(ChatRole.System, $"User preferences: {prefText}"));
        }

        if (options.IncludeReasoningTraces && context.SimilarTraces.Items.Count > 0)
        {
            var traceText = string.Join("; ", context.SimilarTraces.Items
                .Select(t => t.Task));
            messages.Add(new ChatMessage(ChatRole.System, $"Similar past tasks: {traceText}"));
        }

        if (!string.IsNullOrEmpty(context.GraphRagContext))
            messages.Add(new ChatMessage(ChatRole.System, context.GraphRagContext));

        return messages.Take(options.MaxContextMessages).ToList();
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
