using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework.Mapping;

namespace AgentMemory.AgentFramework;

/// <summary>
/// Wraps <see cref="IMemoryService"/> to provide MAF-compatible message persistence.
/// </summary>
public sealed class Neo4jChatMessageStore
{
    private readonly IMemoryService _memoryService;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<Neo4jChatMessageStore> _logger;

    public Neo4jChatMessageStore(
        IMemoryService memoryService,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<Neo4jChatMessageStore> logger)
    {
        _memoryService = memoryService ?? throw new ArgumentNullException(nameof(memoryService));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _idGenerator = idGenerator ?? throw new ArgumentNullException(nameof(idGenerator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Persists a single <see cref="ChatMessage"/> into memory and returns the stored <see cref="Message"/>.
    /// </summary>
    public async Task<Message> AddMessageAsync(
        ChatMessage chatMessage,
        string sessionId,
        string conversationId,
        CancellationToken ct = default)
    {
        try
        {
            var message = MafTypeMapper.ToInternalMessage(chatMessage, sessionId, conversationId, _clock, _idGenerator);
            return await _memoryService
                .AddMessageAsync(message.SessionId, message.ConversationId, message.Role, message.Content, message.Metadata, ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Do NOT fabricate a success Message on failure — that silently hides data loss from the caller
            // and would let PersistAfterRun's extraction step run over messages that were never stored.
            // Surface the failure by rethrowing; the caller decides how to log it (the facade's
            // PersistAfterRunAsync logs it at the run boundary). Debug-level here to avoid a duplicate entry.
            _logger.LogDebug(ex, "Failed to add message for session {SessionId}; rethrowing.", sessionId);
            throw;
        }
    }

    /// <summary>
    /// Retrieves recent messages for a session as <see cref="ChatMessage"/> instances.
    /// </summary>
    public async Task<IReadOnlyList<ChatMessage>> GetMessagesAsync(
        string sessionId,
        int limit = 50,
        CancellationToken ct = default)
    {
        try
        {
            var recallResult = await _memoryService.RecallAsync(
                new Abstractions.Domain.RecallRequest
                {
                    SessionId = sessionId,
                    Query = string.Empty,
                    Options = new Abstractions.Options.RecallOptions { MaxRecentMessages = limit }
                }, ct).ConfigureAwait(false);

            // RecentMessages is newest-first (recall orders DESC); return chat history chronologically
            // (oldest-first) so the agent reads the conversation in the order it happened.
            return recallResult.Context.RecentMessages.Items
                .Reverse()
                .Select(MafTypeMapper.ToChatMessage)
                .ToList();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve messages for session {SessionId}.", sessionId);
            return Array.Empty<ChatMessage>();
        }
    }

    /// <summary>
    /// Clears all memory for the given session.
    /// </summary>
    public async Task ClearSessionAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await _memoryService.ClearSessionAsync(sessionId, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear session {SessionId}.", sessionId);
        }
    }
}
