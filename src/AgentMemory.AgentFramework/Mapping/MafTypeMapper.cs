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
    /// Derives a deterministic persistence id from a response <see cref="ChatMessage"/>'s provider-native
    /// <see cref="ChatMessage.MessageId"/>, when the underlying <c>IChatClient</c> populates one (common
    /// for clients backed by e.g. the OpenAI Responses API). Returns null when absent -- caller-constructed
    /// messages (typically request/user messages) essentially never have one, so this only helps dedupe
    /// response messages that independently-configured persisting components (Neo4jMemoryContextProvider,
    /// Neo4jChatHistoryProvider, Neo4jChatMessageStore) might otherwise each persist as a separate node.
    /// </summary>
    public static string? TryGetProviderMessageId(ChatMessage message) =>
        string.IsNullOrWhiteSpace(message.MessageId) ? null : $"maf:{message.MessageId}";

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
            lead.Add(new ChatMessage(ChatRole.System, WrapUntrustedContent("graphrag", context.GraphRagContext)));

        // Chat (truncatable): dedup across RecentMessages and RelevantMessages — a message may appear in
        // both. DistinctBy preserves insertion order (recent-first) while dropping subsequent duplicates.
        var chatMessages = context.RecentMessages.Items
            .Concat(context.RelevantMessages.Items)
            .DistinctBy(m => m.MessageId)
            .Select(ToChatMessage)
            .ToList();

        // Memory-derived system messages (always kept). Each is delimited and escaped (#92 Phase 1) so
        // recalled content -- which may originate from a user, an external document, a tool result, or
        // the model itself -- cannot masquerade as an unrestricted, undelimited system instruction, and
        // cannot forge or prematurely close its own boundary.
        var memory = new List<ChatMessage>();
        if (options.IncludeEntities && context.RelevantEntities.Items.Count > 0)
        {
            var entityText = string.Join(", ", context.RelevantEntities.Items
                .Select(e => string.IsNullOrEmpty(e.Description)
                    ? $"{e.Name} ({e.Type})"
                    : $"{e.Name} ({e.Type}): {e.Description}"));
            memory.Add(new ChatMessage(ChatRole.System, WrapUntrustedContent("entities", $"Relevant entities: {entityText}")));
        }

        if (options.IncludeFacts && context.RelevantFacts.Items.Count > 0)
        {
            var factText = string.Join("; ", context.RelevantFacts.Items
                .Select(f => $"{f.Subject} {f.Predicate} {f.Object}"));
            memory.Add(new ChatMessage(ChatRole.System, WrapUntrustedContent("facts", $"Known facts: {factText}")));
        }

        if (options.IncludePreferences && context.RelevantPreferences.Items.Count > 0)
        {
            var prefText = string.Join("; ", context.RelevantPreferences.Items
                .Select(p => p.PreferenceText));
            memory.Add(new ChatMessage(ChatRole.System, WrapUntrustedContent("preferences", $"User preferences: {prefText}")));
        }

        if (options.IncludeReasoningTraces && context.SimilarTraces.Items.Count > 0)
        {
            var traceText = string.Join("; ", context.SimilarTraces.Items
                .Select(t => t.Task));
            memory.Add(new ChatMessage(ChatRole.System, WrapUntrustedContent("traces", $"Similar past tasks: {traceText}")));
        }

        if (!graphFirst && !string.IsNullOrEmpty(context.GraphRagContext))
            memory.Add(new ChatMessage(ChatRole.System, WrapUntrustedContent("graphrag", context.GraphRagContext)));

        // Fill the budget left over after the always-kept lead + memory with the MOST RECENT chat
        // messages. chatMessages is newest-first (RecentMessages.Items is recall-ordered DESC), so the
        // newest `chatBudget` items are the FRONT of the list — Take(chatBudget). (R6-D: the previous
        // Skip(count - chatBudget) kept the TAIL, i.e. the OLDEST messages, dropping the newest turns
        // first — the opposite of "most recent".) Order is preserved (lead → chat → memory).
        int chatBudget = Math.Max(0, options.MaxContextMessages - lead.Count - memory.Count);
        var keptChat = chatMessages.Count > chatBudget
            ? chatMessages.Take(chatBudget).ToList()
            : chatMessages;

        var result = new List<ChatMessage>(lead.Count + keptChat.Count + memory.Count);
        result.AddRange(lead);
        result.AddRange(keptChat);
        result.AddRange(memory);
        return result;
    }

    /// <summary>
    /// Wraps untrusted recalled content (an entity/fact/preference/trace/GraphRAG block, which may
    /// originate from a user, an external document, a tool result, or the model itself) in a delimited
    /// boundary (#92 Phase 1), so it cannot masquerade as an unrestricted, undelimited system instruction.
    /// Paired with the untrusted-reference-data framing in the default <see cref="ContextFormatOptions.ContextPrefix"/>.
    /// This defeats boundary <em>forgery</em> specifically (content can't fake or close the tag) — it does
    /// not detect or block instruction-like content that never relies on the tag (e.g. plain-language
    /// "ignore previous instructions", role-header conventions, code fences); the prefix instruction, not
    /// this delimiter, is what asks the model to disregard those. It also does not apply to recalled
    /// conversation history (<c>RelevantMessages</c>), which keeps its originally-persisted role — both
    /// are disclosed, explicit follow-up scope for #92, not silently dropped.
    /// </summary>
    internal static string WrapUntrustedContent(string category, string content) =>
        $"""<recalled_memory category="{category}">{EscapeForDelimiter(content)}</recalled_memory>""";

    /// <summary>
    /// Escapes every angle bracket in untrusted content so it cannot contain a literal
    /// <c>&lt;recalled_memory&gt;</c>/<c>&lt;/recalled_memory&gt;</c> (or any other tag) — content can
    /// therefore never prematurely close its own boundary or forge a nested one, the same principle as
    /// HTML-encoding user content before embedding it in markup.
    /// </summary>
    private static string EscapeForDelimiter(string content) =>
        content.Replace("<", "&lt;").Replace(">", "&gt;");

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
