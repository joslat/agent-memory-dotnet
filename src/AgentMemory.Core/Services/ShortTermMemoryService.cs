using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

/// <summary>
/// Service for short-term (conversational) memory operations.
/// </summary>
public sealed class ShortTermMemoryService : IShortTermMemoryService
{
    private readonly IConversationRepository _conversationRepo;
    private readonly IMessageRepository _messageRepo;
    private readonly IReasoningTraceRepository _reasoningTraceRepo;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ShortTermMemoryOptions _options;
    private readonly ILogger<ShortTermMemoryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ShortTermMemoryService"/> class.
    /// </summary>
    public ShortTermMemoryService(
        IConversationRepository conversationRepo,
        IMessageRepository messageRepo,
        IReasoningTraceRepository reasoningTraceRepo,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IClock clock,
        IIdGenerator idGenerator,
        IOptions<ShortTermMemoryOptions> options,
        ILogger<ShortTermMemoryService> logger)
    {
        _conversationRepo = conversationRepo;
        _messageRepo = messageRepo;
        _reasoningTraceRepo = reasoningTraceRepo;
        _embeddingOrchestrator = embeddingOrchestrator;
        _clock = clock;
        _idGenerator = idGenerator;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<Conversation> AddConversationAsync(
        string conversationId,
        string sessionId,
        string? userId = null,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        var conversation = new Conversation
        {
            ConversationId = conversationId,
            SessionId = sessionId,
            UserId = userId,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        _logger.LogDebug("Upserting conversation {ConversationId} for session {SessionId}", conversationId, sessionId);
        return await _conversationRepo.UpsertAsync(conversation, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<Message> AddMessageAsync(
        Message message,
        CancellationToken cancellationToken = default)
    {
        var finalMessage = message;

        if (_options.GenerateEmbeddings && message.Embedding is null)
        {
            _logger.LogDebug("Generating embedding for message {MessageId}", message.MessageId);
            var embedding = await _embeddingOrchestrator.EmbedMessageAsync(message.Content, cancellationToken);
            finalMessage = message with { Embedding = embedding };
        }

        return await _messageRepo.AddAsync(finalMessage, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        var results = new List<Message>(messageList.Count);

        foreach (var message in messageList)
        {
            var finalMessage = message;
            if (_options.GenerateEmbeddings && message.Embedding is null)
            {
                var embedding = await _embeddingOrchestrator.EmbedMessageAsync(message.Content, cancellationToken);
                finalMessage = message with { Embedding = embedding };
            }
            results.Add(finalMessage);
        }

        _logger.LogDebug("Batch adding {Count} messages", results.Count);
        return await _messageRepo.AddBatchAsync(results, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        string sessionId,
        int? limit = null,
        CancellationToken cancellationToken = default)
    {
        // A null limit means "use the configured default"; the effective value is then capped by the
        // per-query maximum. (A non-nullable `= 10` default could not distinguish "omitted" from "explicitly
        // 10", which is why DefaultRecentMessageLimit was previously unreadable.)
        var requested = limit ?? _options.DefaultRecentMessageLimit;
        var cappedLimit = Math.Min(requested, _options.MaxMessagesPerQuery);
        return await _messageRepo.GetRecentBySessionAsync(sessionId, cappedLimit, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Message>> GetAllSessionMessagesAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        // Deliberately bypasses the MaxMessagesPerQuery cap: whole-session extraction must see every
        // message, not just the most recent page.
        return _messageRepo.GetAllBySessionAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<Message>> GetConversationMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        return _messageRepo.GetByConversationAsync(conversationId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Message>> SearchMessagesAsync(
        string? sessionId,
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _messageRepo.SearchByVectorAsync(
            queryEmbedding, sessionId, limit, minScore, null, cancellationToken);
        return scored.Select(r => r.Message).ToList();
    }

    /// <inheritdoc/>
    public async Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Clearing session {SessionId}", sessionId);
        await _messageRepo.DeleteBySessionAsync(sessionId, cancellationToken);
        await _conversationRepo.DeleteBySessionAsync(sessionId, cancellationToken);
        await _reasoningTraceRepo.DeleteBySessionAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Message>> GetRecentMessagesAsOfAsync(
        string sessionId,
        DateTimeOffset asOf,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var cappedLimit = Math.Min(limit, _options.MaxMessagesPerQuery);
        return await _messageRepo.GetRecentBySessionAsOfAsync(sessionId, asOf, cappedLimit, cancellationToken);
    }
}
