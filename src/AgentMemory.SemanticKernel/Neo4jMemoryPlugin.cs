using System.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.SemanticKernel;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;

namespace AgentMemory.SemanticKernel;

/// <summary>
/// Semantic Kernel native-function plugin that bridges <see cref="IMemoryService"/> to the kernel.
/// Register via <see cref="KernelMemoryExtensions.AddNeo4jMemoryPlugin(IKernelBuilder)"/>.
/// </summary>
public sealed class Neo4jMemoryPlugin
{
    private readonly IMemoryService _memoryService;
    private readonly ILogger<Neo4jMemoryPlugin> _logger;

    /// <summary>Initializes a new instance of <see cref="Neo4jMemoryPlugin"/>.</summary>
    public Neo4jMemoryPlugin(IMemoryService memoryService, ILogger<Neo4jMemoryPlugin>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(memoryService);
        _memoryService = memoryService;
        _logger = logger ?? NullLogger<Neo4jMemoryPlugin>.Instance;
    }

    /// <summary>Recalls relevant memory context for the given query and session.</summary>
    [KernelFunction("recall")]
    [Description("Recall relevant memory context (entities, facts, preferences, recent messages) for a query")]
    public async Task<string> RecallAsync(
        [Description("The user query or topic to recall memories for")] string query,
        [Description("Session identifier")] string sessionId,
        [Description("Optional owner/user identifier. Null recalls across all owners (no owner filter); set it to recall only that user's plus shared/global memories. Set it in multi-tenant deployments to prevent cross-owner reads (R1).")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        var request = new RecallRequest { SessionId = sessionId, UserId = userId, Query = query };
        RecallResult result;
        try
        {
            result = await _memoryService.RecallAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "recall failed for session {SessionId}; returning empty context", sessionId);
            return string.Empty;
        }
        return MemoryContextFormatter.FormatRecallResult(result);
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
        [Description("Optional owner/user identifier (R1). When set, extracted memories are owner-stamped and resolution is owner-scoped; null = stored as shared/global.")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        await _memoryService.ExtractFromSessionAsync(sessionId, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Triggers extraction from all stored messages in a conversation.</summary>
    [KernelFunction("extract_from_conversation")]
    [Description("Extract and persist entities, facts, preferences and relationships from all messages in a conversation")]
    public async Task ExtractFromConversationAsync(
        [Description("Conversation identifier to extract from")] string conversationId,
        [Description("Optional owner/user identifier (R1). When set, extracted memories are owner-stamped and resolution is owner-scoped; null = stored as shared/global.")] string? userId = null,
        CancellationToken cancellationToken = default)
    {
        await _memoryService.ExtractFromConversationAsync(conversationId, userId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Clears all short-term and long-term memory for the given session.</summary>
    [KernelFunction("clear_session")]
    [Description("Clear all memory (messages, entities, facts) for a session")]
    public async Task ClearSessionAsync(
        [Description("Session identifier to clear")] string sessionId,
        CancellationToken cancellationToken = default)
    {
        await _memoryService.ClearSessionAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
