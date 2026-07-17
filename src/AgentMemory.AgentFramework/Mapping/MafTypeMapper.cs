using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Security;
using AgentMemory.Core.Security;

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
        ContextFormatOptions? formatOptions = null,
        IMemoryContextAdmissionPolicy? admissionPolicy = null,
        ILogger? logger = null)
    {
        var options = formatOptions ?? new ContextFormatOptions();
        var admission = admissionPolicy ?? new DefaultMemoryContextAdmissionPolicy();

        // #92 Phase 2: evaluate a candidate item through the admission policy before it contributes to a
        // category's rendered block. Per-ITEM, not per-block: entities/facts/preferences/traces each join
        // several independent items into one string, and evaluating the whole joined string would mean one
        // flagged item (a false positive or a genuinely planted one) silently drops every OTHER, unrelated,
        // legitimate item concatenated alongside it. GraphRagContext is the one exception -- it arrives as
        // a single opaque string, not a list of items, so it is unavoidably evaluated as one block.
        // Every admitted item is still delimited/escaped via WrapUntrustedContent regardless (#92 Phase 1)
        // -- admission only controls whether it appears at all (Strict mode) vs. is quoted either way
        // (Permissive, the default).
        bool Admit(string category, string content, MemoryTrustLevel trustLevel = MemoryTrustLevel.Untrusted) =>
            AdmitItem(category, content, trustLevel, options, admission, logger);

        // The chat-message role a recalled ITEM's BLOCK renders at (#92 Phase 4): items at/above
        // MinimumTrustForSystemRole get DefaultMemoryRole (System by default -- unchanged pre-Phase-4
        // behavior); everything else renders at the fixed lower-authority role, User. Named distinctly from
        // RecalledMessageRoleGate.EffectiveRole (#92 Phase 7, used below for recalled MESSAGES) -- these are
        // two different decisions (entity/fact/preference/GraphRAG block role vs. chat-message role) that
        // happen to share a name pattern; kept separate on purpose, not a duplicate.
        RecalledMemoryMessageRole EffectiveBlockRole(MemoryTrustLevel trustLevel) =>
            trustLevel >= options.MinimumTrustForSystemRole ? options.DefaultMemoryRole : RecalledMemoryMessageRole.User;

        // The single point where RecalledMemoryMessageRole maps to the MEAI ChatRole it renders as --
        // every call site below goes through this (or EffectiveChatRole) rather than re-deriving it.
        ChatRole ToChatRole(RecalledMemoryMessageRole role) =>
            role == RecalledMemoryMessageRole.System ? ChatRole.System : ChatRole.User;

        ChatRole EffectiveChatRole(MemoryTrustLevel trustLevel) => ToChatRole(EffectiveBlockRole(trustLevel));

        // Renders a list-shaped category's (entities/facts/preferences/traces) admitted items into up to
        // two messages, one per effective role (#92 Phase 4) -- see the granularity note on Admit above for
        // why admission itself also filters per item, not per block. Splitting by role the same way means a
        // single ApplicationTrusted item bundled alongside several lower-trust ones still renders at the
        // higher-authority role while the rest render at the lower one, instead of one block-wide decision.
        // Each item's trust level (#92 Phase 3) is read from its own Metadata via GetTrustLevel().
        List<ChatMessage> CategoryMessages<T>(
            string category, IReadOnlyList<T> items, Func<T, string> describe, Func<T, MemoryTrustLevel> getTrustLevel,
            string prefix, string separator)
        {
            var byRole = items
                .Select(item => (Text: describe(item), Trust: getTrustLevel(item)))
                .Where(x => Admit(category, x.Text, x.Trust))
                .GroupBy(x => EffectiveBlockRole(x.Trust))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Text).ToList());

            var messages = new List<ChatMessage>();
            if (byRole.TryGetValue(RecalledMemoryMessageRole.System, out var systemTexts) && systemTexts.Count > 0)
                messages.Add(new ChatMessage(ToChatRole(RecalledMemoryMessageRole.System),
                    WrapUntrustedContent(category, $"{prefix}{string.Join(separator, systemTexts)}")));
            if (byRole.TryGetValue(RecalledMemoryMessageRole.User, out var userTexts) && userTexts.Count > 0)
                messages.Add(new ChatMessage(ToChatRole(RecalledMemoryMessageRole.User),
                    WrapUntrustedContent(category, $"{prefix}{string.Join(separator, userTexts)}")));
            return messages;
        }

        // Build chat messages and memory-derived system messages into SEPARATE buckets and budget them
        // independently. The whole point of this provider is to inject long-term memory; appending memory
        // AFTER chat and then Take()-ing a shared budget put memory at the tail, so once a conversation had
        // enough chat messages the memory blocks were the first dropped — silently injecting ZERO
        // long-term memory. Memory items are always kept; only the chat portion (MaxChatHistoryMessages,
        // independent of lead/memory counts — #91) is truncated.

        // Lead (always kept): optional prefix + graph context when it leads (GraphRagOnly/GraphRagThenMemory).
        var lead = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(options.ContextPrefix))
            lead.Add(new ChatMessage(ChatRole.System, options.ContextPrefix));

        bool graphFirst = context.BlendMode is RetrievalBlendMode.GraphRagOnly or RetrievalBlendMode.GraphRagThenMemory;
        // GraphRAG has no per-item metadata (a single opaque string, not a list of items), so it's always
        // evaluated at Untrusted -- computed once and reused at whichever of the two placements below
        // applies (they're mutually exclusive per `graphFirst`, but the role must still agree between them).
        var graphRagRole = EffectiveChatRole(MemoryTrustLevel.Untrusted);
        if (graphFirst && !string.IsNullOrEmpty(context.GraphRagContext) && Admit("graphrag", context.GraphRagContext))
            lead.Add(new ChatMessage(graphRagRole, WrapUntrustedContent("graphrag", context.GraphRagContext)));

        // Chat (truncatable): dedup across RecentMessages and RelevantMessages — a message may appear in
        // both. DistinctBy preserves insertion order (recent-first) while dropping subsequent duplicates.
        // #92 Phase 7 gated the recalled ROLE: a message persisted with a privileged role ("system"/"tool")
        // via a caller-facing tool (memory_store_message, Neo4jMemoryPlugin.AddMessageAsync) could otherwise
        // resurface here with full, undiminished ChatRole.System/Tool authority. This method's siblings
        // Neo4jChatMessageStore/Neo4jChatHistoryProvider, which read the SAME RecentMessages data for
        // genuine same-session chat-history replay, were found to skip this gating entirely during a
        // stabilization audit and now apply it too, via the shared ToGatedChatMessages helper below.
        // #92 Phase 8: message CONTENT now also goes through the same per-item admission check as every
        // other category (Strict mode excludes instruction-like content; Permissive, the default, still
        // includes it). Deliberately NOT delimited/wrapped like entities/facts/preferences/GraphRAG --
        // unlike those, a recalled message renders as an individual turn within the actual conversation
        // history the model reads, so wrapping its content in visible <recalled_memory> tags would make
        // ordinary chat history look bizarre for comparatively little added security value once the role
        // itself is gated. Admission (include/exclude) is the appropriately-scoped protection here, not
        // delimiting (which defeats boundary forgery a wrapped block doesn't need to defend against).
        var chatMessages = context.RecentMessages.Items
            .Concat(context.RelevantMessages.Items)
            .DistinctBy(m => m.MessageId)
            .Select(m => (Message: m, TrustLevel: m.Metadata.GetTrustLevel()))
            .Where(x => Admit("messages", x.Message.Content, x.TrustLevel))
            .Select(x => ToChatMessage(x.Message with
            {
                Role = RecalledMessageRoleGate.EffectiveRole(
                    x.Message.Role, x.TrustLevel, options.MinimumTrustForSystemRole)
            }))
            .ToList();

        // Memory-derived system messages (always kept). Each is delimited and escaped (#92 Phase 1) so
        // recalled content -- which may originate from a user, an external document, a tool result, or
        // the model itself -- cannot masquerade as an unrestricted, undelimited system instruction, and
        // cannot forge or prematurely close its own boundary.
        var memory = new List<ChatMessage>();
        if (options.IncludeEntities && context.RelevantEntities.Items.Count > 0)
            memory.AddRange(CategoryMessages("entities", context.RelevantEntities.Items,
                e => string.IsNullOrEmpty(e.Description) ? $"{e.Name} ({e.Type})" : $"{e.Name} ({e.Type}): {e.Description}",
                e => e.Metadata.GetTrustLevel(), "Relevant entities: ", ", "));

        if (options.IncludeFacts && context.RelevantFacts.Items.Count > 0)
            memory.AddRange(CategoryMessages("facts", context.RelevantFacts.Items,
                f => $"{f.Subject} {f.Predicate} {f.Object}",
                f => f.Metadata.GetTrustLevel(), "Known facts: ", "; "));

        if (options.IncludePreferences && context.RelevantPreferences.Items.Count > 0)
            memory.AddRange(CategoryMessages("preferences", context.RelevantPreferences.Items, p => p.PreferenceText,
                p => p.Metadata.GetTrustLevel(), "User preferences: ", "; "));

        if (options.IncludeReasoningTraces && context.SimilarTraces.Items.Count > 0)
            memory.AddRange(CategoryMessages("traces", context.SimilarTraces.Items, t => t.Task,
                t => t.Metadata.GetTrustLevel(), "Similar past tasks: ", "; "));

        if (!graphFirst && !string.IsNullOrEmpty(context.GraphRagContext) && Admit("graphrag", context.GraphRagContext))
            memory.Add(new ChatMessage(graphRagRole, WrapUntrustedContent("graphrag", context.GraphRagContext)));

        // MaxChatHistoryMessages bounds ONLY recalled chat history -- it is independent of lead/memory
        // counts (#91): entities/facts/preferences/traces/GraphRAG/the prefix are durable long-term
        // memory and are never truncated to make room for chat, so they are not subtracted from this
        // budget. chatMessages is newest-first (RecentMessages.Items is recall-ordered DESC), so the
        // newest `chatBudget` items are the FRONT of the list — Take(chatBudget). (R6-D: the previous
        // Skip(count - chatBudget) kept the TAIL, i.e. the OLDEST messages, dropping the newest turns
        // first — the opposite of "most recent".) Order is preserved (lead → chat → memory).
        int chatBudget = Math.Max(0, options.MaxChatHistoryMessages);
        var keptChat = chatMessages.Count > chatBudget
            ? chatMessages.Take(chatBudget).ToList()
            : chatMessages;

        var result = new List<ChatMessage>(lead.Count + keptChat.Count + memory.Count);
        result.AddRange(lead);
        result.AddRange(keptChat);
        result.AddRange(memory);
        return result;
    }

    // Extracted from ToContextMessages' local Admit closure (stabilization fix) so it can be shared with
    // ToGatedChatMessages below, instead of the two call sites re-deriving the same admission decision and
    // logging independently. Behavior is unchanged from the closure this replaces.
    private static bool AdmitItem(
        string category, string content, MemoryTrustLevel trustLevel,
        ContextFormatOptions options, IMemoryContextAdmissionPolicy admission, ILogger? logger)
    {
        var decision = admission.Evaluate(new MemoryAdmissionContext
        {
            Category = category,
            Content = content,
            Mode = options.SecurityMode,
            TrustLevel = trustLevel,
            MinimumTrustForAdmissionBypass = options.MinimumTrustForAdmissionBypass
        });

        // Flagged-but-included (Permissive, the default) is still observable -- Debug, not Warning,
        // since nothing was actually excluded.
        if (decision.InstructionLikeContentDetected && decision.Include)
        {
            logger?.LogDebug(
                "Recalled memory item in category '{Category}' flagged as instruction-like content " +
                "but included (SecurityMode={Mode}).", category, options.SecurityMode);
        }

        if (!decision.Include)
        {
            logger?.LogWarning(
                "Excluded a recalled memory item in category '{Category}' from context: {Reason}.",
                category, decision.ExclusionReason ?? "unspecified");
        }

        return decision.Include;
    }

    /// <summary>
    /// Applies the same per-item admission check (#92 Phase 2/8) and privileged-role gating (#92 Phase 7)
    /// <see cref="ToContextMessages"/> applies to <c>RecentMessages</c>/<c>RelevantMessages</c>, to any OTHER
    /// surface that replays a session's own chat history back to it -- <c>Neo4jChatHistoryProvider</c> and
    /// <c>Neo4jChatMessageStore.GetMessagesAsync</c> were found during a stabilization audit to skip both
    /// protections entirely, mapping messages through the bare <see cref="ToChatMessage"/> with no gating at
    /// all. A message persisted with a privileged role via <c>memory_store_message</c> (MCP) or
    /// <c>Neo4jMemoryPlugin.AddMessageAsync</c> (SK) -- both accept an unvalidated caller-supplied role --
    /// would otherwise replay with full, unrestricted authority on every future turn of the SAME session, not
    /// only via cross-session recall. Like every other gate in this file, this is a no-op under default
    /// options: it only changes behavior once a host raises <c>SecurityMode</c> to <c>Strict</c> or raises
    /// <c>MinimumTrustForSystemRole</c> above <c>Untrusted</c>. Message content is admission-checked but
    /// never delimited, matching <see cref="ToContextMessages"/>'s treatment of chat history (#92 Phase 8).
    /// </summary>
    internal static List<ChatMessage> ToGatedChatMessages(
        IEnumerable<Message> messages,
        ContextFormatOptions options,
        IMemoryContextAdmissionPolicy admissionPolicy,
        ILogger? logger = null)
    {
        return messages
            .Select(m => (Message: m, TrustLevel: m.Metadata.GetTrustLevel()))
            .Where(x => AdmitItem("chathistory", x.Message.Content, x.TrustLevel, options, admissionPolicy, logger))
            .Select(x => ToChatMessage(x.Message with
            {
                Role = RecalledMessageRoleGate.EffectiveRole(x.Message.Role, x.TrustLevel, options.MinimumTrustForSystemRole)
            }))
            .ToList();
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
    /// conversation history (<c>RelevantMessages</c>) -- message content there is deliberately left
    /// undelimited (#92 Phase 8), since wrapping an individual recalled chat turn in visible
    /// <c>&lt;recalled_memory&gt;</c> tags would make ordinary conversation history look bizarre; it is
    /// still admission-checked per item (same instruction-like-content detection as every other category),
    /// just not delimited. Its ROLE, however, is NOT simply kept as originally persisted:
    /// a privileged role ("system"/"tool") is gated separately (#92 Phase 7, see
    /// <c>AgentMemory.Core.Security.RecalledMessageRoleGate</c>) rather than replayed unconditionally, since
    /// the "original" role itself can be caller-controlled. Delegates to
    /// <c>AgentMemory.Core.Security.RecalledMemoryDelimiter</c> (relocated there in #92 Phase 6 so the
    /// Semantic Kernel adapter can share the same delimiting/escaping logic).
    /// </summary>
    internal static string WrapUntrustedContent(string category, string content) =>
        AgentMemory.Core.Security.RecalledMemoryDelimiter.Wrap(category, content);

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
