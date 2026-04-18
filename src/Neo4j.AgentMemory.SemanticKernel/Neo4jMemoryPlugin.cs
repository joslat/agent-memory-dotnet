using System.ComponentModel;
using System.Text;
using Microsoft.SemanticKernel;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.SemanticKernel;

/// <summary>
/// Semantic Kernel native-function plugin that bridges <see cref="IMemoryService"/> to the kernel.
/// Register via <see cref="KernelMemoryExtensions.AddNeo4jMemoryPlugin(IKernelBuilder)"/>.
/// </summary>
public sealed class Neo4jMemoryPlugin
{
    private readonly IMemoryService _memoryService;

    /// <summary>Initializes a new instance of <see cref="Neo4jMemoryPlugin"/>.</summary>
    public Neo4jMemoryPlugin(IMemoryService memoryService)
    {
        _memoryService = memoryService;
    }

    /// <summary>Recalls relevant memory context for the given query and session.</summary>
    [KernelFunction("recall")]
    [Description("Recall relevant memory context (entities, facts, preferences, recent messages) for a query")]
    public async Task<string> RecallAsync(
        [Description("The user query or topic to recall memories for")] string query,
        [Description("Session identifier")] string sessionId,
        [Description("Optional conversation identifier to narrow recall scope")] string? conversationId = null,
        CancellationToken cancellationToken = default)
    {
        var request = new RecallRequest { SessionId = sessionId, Query = query };
        RecallResult result;
        try
        {
            result = await _memoryService.RecallAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return string.Empty;
        }
        return FormatRecallResult(result);
    }

    /// <summary>Adds a single message to short-term memory.</summary>
    [KernelFunction("add_message")]
    [Description("Add a message to agent memory for the current session")]
    public async Task AddMessageAsync(
        [Description("Session identifier")] string sessionId,
        [Description("Conversation identifier")] string conversationId,
        [Description("Role of the sender (e.g. 'user', 'assistant', 'system')")] string role,
        [Description("Text content of the message")] string content,
        CancellationToken cancellationToken = default)
    {
        await _memoryService.AddMessageAsync(sessionId, conversationId, role, content, null, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Triggers extraction from all stored messages in a session.</summary>
    [KernelFunction("extract_from_session")]
    [Description("Extract and persist entities, facts, preferences and relationships from all messages in a session")]
    public async Task ExtractFromSessionAsync(
        [Description("Session identifier to extract from")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _memoryService.ExtractFromSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Triggers extraction from all stored messages in a conversation.</summary>
    [KernelFunction("extract_from_conversation")]
    [Description("Extract and persist entities, facts, preferences and relationships from all messages in a conversation")]
    public async Task ExtractFromConversationAsync(
        [Description("Conversation identifier to extract from")] string conversationId,
        CancellationToken cancellationToken = default)
    {
        await _memoryService.ExtractFromConversationAsync(conversationId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears all short-term and long-term memory for the given session.</summary>
    [KernelFunction("clear_session")]
    [Description("Clear all memory (messages, entities, facts) for a session")]
    public async Task ClearSessionAsync(
        [Description("Session identifier to clear")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _memoryService.ClearSessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
    }

    // ── Formatting ─────────────────────────────────────────────────────────────

    internal static string FormatRecallResult(RecallResult result)
    {
        if (result.TotalItemsRetrieved == 0)
            return string.Empty;

        var ctx = result.Context;
        var sb = new StringBuilder();
        sb.AppendLine("## Memory Context");
        AppendMessages(sb, "### Recent Messages", ctx.RecentMessages);
        AppendMessages(sb, "### Relevant Past Messages", ctx.RelevantMessages);
        AppendEntities(sb, ctx.RelevantEntities);
        AppendFacts(sb, ctx.RelevantFacts);
        AppendPreferences(sb, ctx.RelevantPreferences);
        if (!string.IsNullOrWhiteSpace(ctx.GraphRagContext))
        {
            sb.AppendLine("### Graph Context");
            sb.AppendLine(ctx.GraphRagContext);
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendMessages(StringBuilder sb, string heading, MemoryContextSection<Message> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine(heading);
        foreach (var msg in section.Items)
            sb.AppendLine($"[{msg.Role}]: {msg.Content}");
        sb.AppendLine();
    }

    private static void AppendEntities(StringBuilder sb, MemoryContextSection<Entity> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine("### Known Entities");
        foreach (var entity in section.Items)
        {
            var desc = string.IsNullOrWhiteSpace(entity.Description) ? string.Empty : $" — {entity.Description}";
            sb.AppendLine($"- {entity.Name} ({entity.Type}){desc}");
        }
        sb.AppendLine();
    }

    private static void AppendFacts(StringBuilder sb, MemoryContextSection<Fact> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine("### Known Facts");
        foreach (var fact in section.Items)
            sb.AppendLine($"- {fact.Subject} {fact.Predicate} {fact.Object}");
        sb.AppendLine();
    }

    private static void AppendPreferences(StringBuilder sb, MemoryContextSection<Preference> section)
    {
        if (section.Items.Count == 0) return;
        sb.AppendLine("### User Preferences");
        foreach (var pref in section.Items)
            sb.AppendLine($"- [{pref.Category}] {pref.PreferenceText}");
        sb.AppendLine();
    }
}
