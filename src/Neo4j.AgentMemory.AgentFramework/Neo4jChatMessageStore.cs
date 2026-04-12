using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework.Mapping;

namespace Neo4j.AgentMemory.AgentFramework;

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
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add message for session {SessionId}.", sessionId);
            return new Message
            {
                MessageId = _idGenerator.GenerateId(),
                SessionId = sessionId,
                ConversationId = conversationId,
                Role = MafTypeMapper.ToInternalRole(chatMessage.Role),
                Content = chatMessage.Text ?? string.Empty,
                TimestampUtc = _clock.UtcNow
            };
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

            return recallResult.Context.RecentMessages.Items
                .Select(MafTypeMapper.ToChatMessage)
                .ToList();
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
            await _memoryService.ClearSessionAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear session {SessionId}.", sessionId);
        }
    }
}
