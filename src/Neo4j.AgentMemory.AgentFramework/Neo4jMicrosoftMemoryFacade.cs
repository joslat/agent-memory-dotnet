using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework.Mapping;

namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// Convenience facade that combines context provision, message storage, and extraction
/// for use in MAF agent pipelines.
/// </summary>
public sealed class Neo4jMicrosoftMemoryFacade
{
    private readonly IMemoryService _memoryService;
    private readonly Neo4jChatMessageStore _messageStore;
    private readonly AgentFrameworkOptions _options;
    private readonly ILogger<Neo4jMicrosoftMemoryFacade> _logger;

    public Neo4jMicrosoftMemoryFacade(
        IMemoryService memoryService,
        Neo4jChatMessageStore messageStore,
        IOptions<AgentFrameworkOptions> options,
        ILogger<Neo4jMicrosoftMemoryFacade> logger)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _messageStore = messageStore ?? throw new ArgumentNullException(nameof(messageStore));
        _options = options?.Value ?? new AgentFrameworkOptions();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Pre-run: retrieves recent messages from memory for the given session to seed context.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> GetContextForRunAsync(
        IReadOnlyList<ChatMessage> messages,
        string sessionId,
        string conversationId,
        CancellationToken ct = default)
    {
        try
        {
            return await _messageStore.GetMessagesAsync(sessionId, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get context for session {SessionId}.", sessionId);
            return Array.Empty<ChatMessage>();
        }
    }

    /// <summary>
    /// Post-run: persists the provided messages and optionally triggers extraction.
    /// </summary>
    public async Task PersistAfterRunAsync(
        IReadOnlyList<ChatMessage> messages,
        string sessionId,
        string conversationId,
        CancellationToken ct = default)
    {
        if (messages.Count == 0)
            return;

        try
        {
            var internalMessages = new List<Message>();
            foreach (var msg in messages)
            {
                var stored = await _messageStore
                    .AddMessageAsync(msg, sessionId, conversationId, ct)
                    .ConfigureAwait(false);
                internalMessages.Add(stored);
            }

            if (_options.AutoExtractOnPersist && internalMessages.Count > 0)
            {
                try
                {
                    await _memoryService.ExtractAndPersistAsync(
                        new ExtractionRequest
                        {
                            Messages = internalMessages,
                            SessionId = sessionId
                        }, ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Extraction failed for session {SessionId}; messages were persisted.", sessionId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist messages for session {SessionId}.", sessionId);
        }
    }

    /// <summary>
    /// Persists a single message into memory.
    /// </summary>
    public Task<Message> StoreMessageAsync(
        ChatMessage msg,
        string sessionId,
        string conversationId,
        CancellationToken ct = default)
        => _messageStore.AddMessageAsync(msg, sessionId, conversationId, ct);

    /// <summary>
    /// Clears all memory for the given session.
    /// </summary>
    public Task ClearSessionAsync(string sessionId, CancellationToken ct = default)
        => _messageStore.ClearSessionAsync(sessionId, ct);
}
