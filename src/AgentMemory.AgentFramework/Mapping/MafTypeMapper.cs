using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Security;
using AgentMemory.Core.Security;
using AgentMemory.Core.Services.Projection;

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
        ILogger? logger = null,
        IReadOnlyList<ChatMessage>? liveThread = null)
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
            string prefix, string separator, Func<T, string>? idOf = null)
        {
            // 30.2. Identity when context.Projection is null, which is the default and the state every
            // sealed prompt fingerprint was taken under.
            var projection = context.Projection;

            var byRole = items
                .Select(item => (Item: item, Text: describe(item), Trust: getTrustLevel(item)))
                .Where(x => Admit(category, x.Text, x.Trust))
                // Annotate AFTER admission, then re-admit what projection added: a source quote is
                // recalled MESSAGE content spliced onto a fact line, so leaving it unchecked would let
                // instruction-like text ride in behind a triple that had already passed -- bypassing
                // the check for exactly the content most worth checking. On failure the item keeps its
                // base text rather than being dropped; it was already judged admissible, and losing it
                // over a suspect decoration would be silent retrieval loss.
                .Select(x => (
                    Text: AnnotateAndAdmit(category, x.Text, x.Item, x.Trust, projection, idOf, Admit),
                    x.Trust))
                .GroupBy(x => EffectiveBlockRole(x.Trust))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Text).ToList());

            var messages = new List<ChatMessage>();
            if (byRole.TryGetValue(RecalledMemoryMessageRole.System, out var systemTexts) && systemTexts.Count > 0)
                messages.Add(new ChatMessage(ToChatRole(RecalledMemoryMessageRole.System),
                    WrapUntrustedContent(category, $"{prefix}{string.Join(separator, systemTexts)}")));
            if (byRole.TryGetValue(RecalledMemoryMessageRole.User, out var userTexts) && userTexts.Count > 0)
                messages.Add(new ChatMessage(ToChatRole(RecalledMemoryMessageRole.User),
                    WrapUntrustedContent(category, $"{prefix}{string.Join(separator, userTexts)}")));

            // Section-level blocks (no-direct-match, conflicts) join the same bucket as their own
            // delimited message at the lower-authority role. They describe recalled memory, so they get
            // recalled memory's authority -- never the system role.
            var preamble = ProjectionRenderer.SectionPreamble(category, projection);
            if (!string.IsNullOrWhiteSpace(preamble) && Admit(category, preamble))
            {
                messages.Insert(0, new ChatMessage(
                    EffectiveChatRole(MemoryTrustLevel.Untrusted),
                    WrapUntrustedContent(category, preamble)));
            }

            return messages;
        }

        // One admitted trace's rendered text. The outcome is appended rather than replacing the task:
        // "what was attempted" is what makes a recalled outcome interpretable, and a procedure without
        // its task reads as a bare instruction -- exactly the shape the admission policy is watching for.
        //
        // Joined with ": " and not an arrow, because every admitted block is HTML-escaped (#92 Phase 1):
        // a "->" separator renders to the model as "-&gt;". Matching the format MemoryQueryFacade already
        // uses for a trace ("task: outcome") keeps one shape across both surfaces.
        string DescribeTrace(ReasoningTrace trace) =>
            options.IncludeTraceOutcomes && !string.IsNullOrWhiteSpace(trace.Outcome)
                ? $"{trace.Task}: {trace.Outcome}"
                : trace.Task;

        // Build chat messages and memory-derived system messages into SEPARATE buckets and budget them
        // independently. The whole point of this provider is to inject long-term memory; appending memory
        // AFTER chat and then Take()-ing a shared budget put memory at the tail, so once a conversation had
        // enough chat messages the memory blocks were the first dropped — silently injecting ZERO
        // long-term memory. Memory items are always kept; only the chat portion (MaxChatHistoryMessages,
        // independent of lead/memory counts — #91) is truncated.

        // Lead (always kept): optional prefix + graph context when it leads (GraphRagOnly/GraphRagThenMemory).
        var lead = new List<ChatMessage>();
        // EffectiveContextPrefix, not ContextPrefix: with trace outcomes on it carries the one narrow
        // exception that lets the agent reuse its own previously-successful tool ordering (25.3).
        // Without it the prefix tells the model to ignore exactly what procedural memory supplies.
        if (!string.IsNullOrWhiteSpace(options.EffectiveContextPrefix))
            lead.Add(new ChatMessage(ChatRole.System, options.EffectiveContextPrefix));

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

        // 30.4. The deterministic tier renders BEFORE the probabilistic sections: it is the head of the
        // question distribution (name, job, stable preferences) and cannot be starved the way a vector
        // section measurably can. Compiled from extraction output, so it is untrusted content and gets
        // the same per-item admission + delimiting as facts -- no trust bypass.
        if (options.IncludeWorkingMemory && !string.IsNullOrWhiteSpace(context.WorkingMemoryBlock))
        {
            memory.AddRange(CategoryMessages(
                "profile",
                context.WorkingMemoryBlock!.Split('\n', StringSplitOptions.RemoveEmptyEntries),
                line => line,
                _ => MemoryTrustLevel.Untrusted,
                string.Empty,
                "\n"));
        }

        if (options.IncludeEntities && context.RelevantEntities.Items.Count > 0)
            memory.AddRange(CategoryMessages("entities", context.RelevantEntities.Items,
                e => string.IsNullOrEmpty(e.Description) ? $"{e.Name} ({e.Type})" : $"{e.Name} ({e.Type}): {e.Description}",
                e => e.Metadata.GetTrustLevel(), "Relevant entities: ", ", ", e => e.EntityId));

        if (options.IncludeFacts && context.RelevantFacts.Items.Count > 0)
            memory.AddRange(CategoryMessages("facts", ProjectionRenderer.Reorder("facts", context.RelevantFacts.Items, f => f.FactId, context.Projection),
                f => $"{f.Subject} {f.Predicate} {f.Object}",
                f => f.Metadata.GetTrustLevel(), "Known facts: ", "; ", f => f.FactId));

        if (options.IncludePreferences && context.RelevantPreferences.Items.Count > 0)
            memory.AddRange(CategoryMessages("preferences", context.RelevantPreferences.Items, p => p.PreferenceText,
                p => p.Metadata.GetTrustLevel(), "User preferences: ", "; ", p => p.PreferenceId));

        // A trace's Task is what was attempted; its Outcome is what happened -- and on a REPEATED task
        // the Task text is something the agent already has, so rendering it alone tells the model it has
        // been here before and nothing about how it got through. That is the whole content of a promoted
        // procedure, so trace recall was structurally unable to convey one (opt-in: see
        // ContextFormatOptions.IncludeTraceOutcomes).
        if (options.IncludeReasoningTraces && context.SimilarTraces.Items.Count > 0)
            memory.AddRange(CategoryMessages("traces", context.SimilarTraces.Items, DescribeTrace,
                t => t.Metadata.GetTrustLevel(), "Similar past tasks: ", "; ", t => t.TraceId));

        if (!graphFirst && !string.IsNullOrEmpty(context.GraphRagContext) && Admit("graphrag", context.GraphRagContext))
            memory.Add(new ChatMessage(graphRagRole, WrapUntrustedContent("graphrag", context.GraphRagContext)));

        // MaxChatHistoryMessages bounds ONLY recalled chat history -- it is independent of lead/memory
        // counts (#91): entities/facts/preferences/traces/GraphRAG/the prefix are durable long-term
        // memory and are never truncated to make room for chat, so they are not subtracted from this
        // budget. chatMessages is newest-first (RecentMessages.Items is recall-ordered DESC), so the
        // newest `chatBudget` items are the FRONT of the list — Take(chatBudget). (R6-D: the previous
        // Skip(count - chatBudget) kept the TAIL, i.e. the OLDEST messages, dropping the newest turns
        // first — the opposite of "most recent".) Order is preserved (lead → chat → memory).
        // The host already sends the live thread, and recall returns the same recent turns from
        // storage, so the model sees them twice. Filtered BEFORE the budget on purpose: dropping
        // duplicates first turns the same `chatBudget` slots into that many genuinely new messages
        // rather than merely shortening the block -- a quality gain, not only a token saving.
        //
        // Fingerprinted on CONTENT ONLY, never role. RecalledMessageRoleGate rewrites a recalled
        // message's role -- privileged to "user" below the trust threshold -- while leaving its
        // content untouched, so a role-keyed fingerprint would fail to match precisely for the hosts
        // that hardened MinimumTrustForSystemRole: the feature would silently no-op for the
        // security-conscious configuration and work everywhere else.
        if (liveThread is { Count: > 0 })
        {
            var live = liveThread
                .Select(message => NormalizeForDedup(message.Text))
                .Where(text => text.Length > 0)
                .ToHashSet(StringComparer.Ordinal);

            if (live.Count > 0)
            {
                var deduped = chatMessages
                    .Where(message => !live.Contains(NormalizeForDedup(message.Text)))
                    .ToList();
                if (deduped.Count != chatMessages.Count)
                {
                    logger?.LogDebug(
                        "Dropped {Dropped} recalled message(s) already present in the live thread.",
                        chatMessages.Count - deduped.Count);
                }
                chatMessages = deduped;
            }
        }

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

    /// <summary>
    /// The dedup key for a chat message: its content, normalised for whitespace and case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Content only.</b> Role is excluded because <c>RecalledMessageRoleGate</c> rewrites a recalled
    /// message's role while leaving its content identical, so keying on role would miss every match on
    /// exactly the hosts that raised <c>MinimumTrustForSystemRole</c>.
    /// </para>
    /// <para>
    /// Whitespace-collapsed and case-folded because the live thread and the stored copy travel
    /// different paths — one through the host's own formatting, one through persistence and back — and
    /// a trailing newline is not a different message. Ordinal comparison after folding, so this stays
    /// culture-independent.
    /// </para>
    /// </remarks>
    internal static string NormalizeForDedup(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .ToUpperInvariant();

    // Extracted from ToContextMessages' local Admit closure (stabilization fix) so it can be shared with
    // ToGatedChatMessages below, instead of the two call sites re-deriving the same admission decision and
    // logging independently. Behavior is unchanged from the closure this replaces.
    /// <summary>
    /// Applies projection to an already-admitted line and re-admits what it added.
    /// </summary>
    /// <remarks>
    /// Shared shape with <c>MemoryContextFormatter.Annotate</c> deliberately — the two surfaces must
    /// make the same security decision about the same content, and this layer exists precisely because
    /// they used to make rendering decisions independently and drift.
    /// </remarks>
    private static string AnnotateAndAdmit<T>(
        string category, string text, T item, MemoryTrustLevel trustLevel,
        ProjectedContext? projection, Func<T, string>? idOf,
        Func<string, string, MemoryTrustLevel, bool> admit)
    {
        if (projection is null || idOf is null) return text;

        var annotated = ProjectionRenderer.AnnotateLine(text, idOf(item), projection);
        if (string.Equals(annotated, text, StringComparison.Ordinal)) return text;

        return admit(category, annotated, trustLevel) ? annotated : text;
    }

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
