using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Mapping;
using AgentMemory.Core.Security;

namespace AgentMemory.AgentFramework;

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
    /// Pre-run: retrieves relevant messages from memory for the given session to seed context.
    /// When <paramref name="messages"/> contains user text, it is used as a semantic query hint
    /// to surface relevant history in addition to recent messages. When empty, falls back to
    /// returning only recent messages.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> GetContextForRunAsync(
        IReadOnlyList<ChatMessage> messages,
        string sessionId,
        string conversationId,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // P2-3: Use user messages as a semantic query hint when available.
            // This surfaces semantically relevant history, not just recency-ordered messages.
            var queryText = string.Join(" ", messages
                .Where(m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text))
                .Select(m => m.Text));

            if (string.IsNullOrWhiteSpace(queryText))
                return await _messageStore.GetMessagesAsync(sessionId, cancellationToken: cancellationToken).ConfigureAwait(false);

            // Owner-scope the recall (R1): the write path (PersistAfterRunAsync / StoreMessageAsync) takes a
            // userId, so the read path must too — otherwise a multi-tenant host would recall across owners.
            var recall = await _memoryService
                .RecallAsync(new RecallRequest { SessionId = sessionId, Query = queryText, UserId = userId }, cancellationToken)
                .ConfigureAwait(false);

            // RecentMessages is newest-first (recall orders DESC); reverse it to chronological
            // (oldest-first) before blending with the semantically-relevant messages so the agent reads
            // recent history in the order it happened. (RelevantMessages ordering is left as-is — it is
            // similarity-ranked, not chronological.)
            // #92 Phase 7: this is a semantic-query recall path (queryText above), the same shape as
            // MafTypeMapper.ToContextMessages' chat-history blending, so a message persisted with a
            // privileged role ("system"/"tool") via a caller-facing tool must be gated here too -- unlike
            // the query-less GetMessagesAsync/GetContextForRunAsync-fallback path above, which relies on
            // Neo4jChatMessageStore for genuine same-session chat-history replay and is intentionally left
            // ungated.
            return recall.Context.RecentMessages.Items
                .Reverse()
                .Concat(recall.Context.RelevantMessages.Items)
                .DistinctBy(m => m.MessageId)
                .Select(m => MafTypeMapper.ToChatMessage(m with
                {
                    Role = RecalledMessageRoleGate.EffectiveRole(
                        m.Role, m.Metadata.GetTrustLevel(), _options.ContextFormat.MinimumTrustForSystemRole)
                }))
                .ToList();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        if (messages.Count == 0)
            return;

        try
        {
            var internalMessages = new List<Message>();
            foreach (var msg in messages)
            {
                var stored = await _messageStore
                    .AddMessageAsync(msg, sessionId, conversationId, cancellationToken)
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
                            SessionId = sessionId,
                            UserId = userId
                        }, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Extraction failed for session {SessionId}; messages were persisted.", sessionId);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
        CancellationToken cancellationToken = default)
        => _messageStore.AddMessageAsync(msg, sessionId, conversationId, cancellationToken);

    /// <summary>
    /// Clears all memory for the given session.
    /// </summary>
    public Task ClearSessionAsync(string sessionId, CancellationToken cancellationToken = default)
        => _messageStore.ClearSessionAsync(sessionId, cancellationToken);
}
