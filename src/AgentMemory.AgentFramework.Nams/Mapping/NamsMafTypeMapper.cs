using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Security;
using AgentMemory.Core.Security;
using AgentMemory.Nams.Persistence;
using AgentMemory.Nams.Recall;

namespace AgentMemory.AgentFramework.Nams.Mapping;

/// <summary>
/// The NAMS equivalent of <c>AgentMemory.AgentFramework.Mapping.MafTypeMapper.ToContextMessages</c> --
/// applies the same #92 protections (per-item admission, delimiting, trust-based role gating) to a
/// <see cref="NamsRecallResult"/> instead of a direct-backend <c>MemoryContext"/>. This is the layer that
/// finally makes Phase 4/5's raw, ungated content (see <see cref="NamsRecalledItem"/>'s own security
/// warning) safe to hand to a model.
/// </summary>
internal static class NamsMafTypeMapper
{
    /// <summary>
    /// Deliberate 1:1 cast -- <see cref="NamsRecallProvenance"/> was built in Phase 4 to mirror
    /// <see cref="MemoryTrustLevel"/> name-for-name/value-for-value specifically for this moment.
    /// </summary>
    public static MemoryTrustLevel ToTrustLevel(NamsRecallProvenance provenance) => (MemoryTrustLevel)(int)provenance;

    public static IReadOnlyList<ChatMessage> ToContextMessages(
        NamsRecallResult recallResult,
        ContextFormatOptions options,
        IMemoryContextAdmissionPolicy admission,
        ILogger? logger = null)
    {
        bool Admit(string category, string content, MemoryTrustLevel trust) =>
            AdmitItem(category, content, trust, options, admission, logger);

        RecalledMemoryMessageRole EffectiveBlockRole(MemoryTrustLevel trust) =>
            trust >= options.MinimumTrustForSystemRole ? options.DefaultMemoryRole : RecalledMemoryMessageRole.User;

        ChatRole ToChatRole(RecalledMemoryMessageRole role) =>
            role == RecalledMemoryMessageRole.System ? ChatRole.System : ChatRole.User;

        var lead = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(options.ContextPrefix))
            lead.Add(new ChatMessage(ChatRole.System, options.ContextPrefix));

        // Chat history (RecentMessage/RelevantMessage): admission-checked, role-gated (a message persisted
        // with a privileged NAMS role demotes below MinimumTrustForSystemRole), NOT delimited -- matches
        // #92 Phase 8's own reasoning: a recalled message renders as an individual conversation turn, not
        // an injected block. Bounded by MaxChatHistoryMessages; Phase 4's recall ordering is already
        // newest-first for this category.
        var chatMessages = recallResult.Items
            .Where(i => i.Category is NamsRecallCategory.RecentMessage or NamsRecallCategory.RelevantMessage)
            .Select(i => (Item: i, Trust: ToTrustLevel(i.Provenance)))
            .Where(x => Admit("messages", x.Item.Content, x.Trust))
            .Select(x => new ChatMessage(
                ToMafRole(RecalledMessageRoleGate.EffectiveRole(x.Item.Role ?? "user", x.Trust, options.MinimumTrustForSystemRole)),
                x.Item.Content))
            .ToList();

        int chatBudget = Math.Max(0, options.MaxChatHistoryMessages);
        var keptChat = chatMessages.Count > chatBudget ? chatMessages.Take(chatBudget).ToList() : chatMessages;

        // Reflections/observations/entities: always kept (durable memory, never truncated by the chat
        // budget -- #91's same reasoning), delimited/escaped (#92 Phase 1), grouped by effective block role.
        var memory = new List<ChatMessage>();
        foreach (var category in new[] { NamsRecallCategory.Reflection, NamsRecallCategory.Observation, NamsRecallCategory.Entity })
        {
            var byRole = recallResult.Items
                .Where(i => i.Category == category)
                .Select(i => (Item: i, Trust: ToTrustLevel(i.Provenance)))
                .Where(x => Admit(CategoryName(category), x.Item.Content, x.Trust))
                .GroupBy(x => EffectiveBlockRole(x.Trust))
                .ToDictionary(g => g.Key, g => g.Select(x => x.Item.Content).ToList());

            if (byRole.TryGetValue(RecalledMemoryMessageRole.System, out var systemTexts) && systemTexts.Count > 0)
                memory.Add(new ChatMessage(ToChatRole(RecalledMemoryMessageRole.System),
                    RecalledMemoryDelimiter.Wrap(CategoryName(category), $"{CategoryPrefix(category)}{string.Join("; ", systemTexts)}")));
            if (byRole.TryGetValue(RecalledMemoryMessageRole.User, out var userTexts) && userTexts.Count > 0)
                memory.Add(new ChatMessage(ToChatRole(RecalledMemoryMessageRole.User),
                    RecalledMemoryDelimiter.Wrap(CategoryName(category), $"{CategoryPrefix(category)}{string.Join("; ", userTexts)}")));
        }
        // NamsRecallCategory.Reasoning is never populated by Phase 4 (no data source exists yet) -- no
        // branch for it here is a deliberate no-op, not a silently dropped category.

        var result = new List<ChatMessage>(lead.Count + keptChat.Count + memory.Count);
        result.AddRange(lead);
        result.AddRange(keptChat);
        result.AddRange(memory);
        return result;
    }

    /// <summary>
    /// Maps a turn's request/response <see cref="ChatMessage"/>s into
    /// <see cref="INamsPersistenceService.PersistTurnAsync"/>'s two parameter lists. Deliberately excludes
    /// non-assistant-role response messages (e.g. tool-call results) rather than mislabeling them as
    /// assistant text -- matches the engineering plan's content policy ("tool calls... recorded through the
    /// reasoning endpoints, not flattened into chat text"), given <c>INamsClient</c> has no reasoning-endpoint
    /// method yet (same disclosed gap as Phases 4/5).
    /// </summary>
    public static (IReadOnlyList<NamsMessageToPersist> UserMessages, IReadOnlyList<NamsMessageToPersist> AssistantMessages) ToPersistenceMessages(
        IEnumerable<ChatMessage> requestMessages, IEnumerable<ChatMessage> responseMessages)
    {
        var userMessages = requestMessages
            .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new NamsMessageToPersist(m.Text))
            .ToList();

        var assistantMessages = responseMessages
            .Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
            .Select(m => new NamsMessageToPersist(m.Text))
            .ToList();

        return (userMessages, assistantMessages);
    }

    private static string CategoryName(NamsRecallCategory category) => category switch
    {
        NamsRecallCategory.Reflection => "nams.reflection",
        NamsRecallCategory.Observation => "nams.observation",
        NamsRecallCategory.RecentMessage => "nams.recent_message",
        NamsRecallCategory.RelevantMessage => "nams.relevant_message",
        NamsRecallCategory.Entity => "nams.entity",
        NamsRecallCategory.Reasoning => "nams.reasoning",
        _ => "nams.unknown"
    };

    /// <summary>Human-readable label prepended to a category's joined block content -- matches the direct
    /// backend's <c>MafTypeMapper.CategoryMessages</c> convention (e.g. "Relevant entities: "), which the
    /// initial NAMS rewrite dropped: the model would otherwise only see the category name via the delimiter
    /// tag's attribute (<c>category="nams.reflection"</c>), never as plain-language framing inside the
    /// visible content itself.</summary>
    private static string CategoryPrefix(NamsRecallCategory category) => category switch
    {
        NamsRecallCategory.Reflection => "Reflections: ",
        NamsRecallCategory.Observation => "Observations: ",
        NamsRecallCategory.Entity => "Relevant entities: ",
        _ => ""
    };

    private static ChatRole ToMafRole(string role) => role switch
    {
        "user" => ChatRole.User,
        "assistant" => ChatRole.Assistant,
        "system" => ChatRole.System,
        "tool" => ChatRole.Tool,
        _ => new ChatRole(role)
    };

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

        if (decision.InstructionLikeContentDetected && decision.Include)
            logger?.LogDebug(
                "Recalled NAMS memory item in category '{Category}' flagged as instruction-like content but included (SecurityMode={Mode}).",
                category, options.SecurityMode);

        if (!decision.Include)
            logger?.LogWarning(
                "Excluded a recalled NAMS memory item in category '{Category}' from context: {Reason}.",
                category, decision.ExclusionReason ?? "unspecified");

        return decision.Include;
    }
}
