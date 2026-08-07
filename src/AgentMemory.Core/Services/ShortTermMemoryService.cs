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
internal sealed class ShortTermMemoryService : IShortTermMemoryService, IScoredMessageSearch
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
        return await _conversationRepo.UpsertAsync(conversation, cancellationToken).ConfigureAwait(false);
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
            var embedding = await _embeddingOrchestrator.EmbedMessageAsync(message.Content, cancellationToken).ConfigureAwait(false);
            finalMessage = message with { Embedding = embedding };
        }

        return await _messageRepo.AddAsync(finalMessage, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default)
    {
        var messageList = messages.ToList();
        if (!_options.GenerateEmbeddings)
        {
            _logger.LogDebug("Batch adding {Count} messages", messageList.Count);
            return await _messageRepo.AddBatchAsync(messageList, cancellationToken).ConfigureAwait(false);
        }

        if (!_options.UseBatchEmbeddingRequests)
        {
            var legacyResults = new List<Message>(messageList.Count);
            foreach (var message in messageList)
            {
                var finalMessage = message;
                if (message.Embedding is null)
                {
                    var embedding = await _embeddingOrchestrator
                        .EmbedMessageAsync(message.Content, cancellationToken)
                        .ConfigureAwait(false);
                    finalMessage = message with { Embedding = embedding };
                }
                legacyResults.Add(finalMessage);
            }

            _logger.LogDebug("Batch adding {Count} messages", legacyResults.Count);
            return await _messageRepo.AddBatchAsync(legacyResults, cancellationToken).ConfigureAwait(false);
        }

        var missingIndices = Enumerable.Range(0, messageList.Count)
            .Where(index => messageList[index].Embedding is null)
            .ToArray();
        if (missingIndices.Length > 0)
        {
            var texts = missingIndices.Select(index => messageList[index].Content).ToArray();
            var embeddings = await _embeddingOrchestrator
                .EmbedBatchAsync(texts, cancellationToken)
                .ConfigureAwait(false);
            if (embeddings.Count != missingIndices.Length)
            {
                throw new InvalidOperationException(
                    $"Batch embedding returned {embeddings.Count} vectors for " +
                    $"{missingIndices.Length} messages; positional alignment cannot be guaranteed.");
            }

            for (var index = 0; index < missingIndices.Length; index++)
            {
                var messageIndex = missingIndices[index];
                var vector = embeddings[index];

                // BUG-M1. The count check above cannot see an empty vector, and an empty list is not
                // null — so such a message passes recall's `embedding IS NOT NULL` filter, then
                // cosine yields null and the score comparison drops it. The message would be stored
                // and permanently unretrievable with no error raised. Replay that slot alone, and if
                // it still comes back unusable, fail the write: a message is the only copy of itself,
                // so losing the write is recoverable and storing an unsearchable one is not.
                // Blank content has no embedding by definition — the orchestrator returns an empty
                // vector for it deliberately. That is a property of the input, not a provider
                // failure, so it is stored as-is and simply never matches a semantic search.
                var contentIsBlank = string.IsNullOrWhiteSpace(messageList[messageIndex].Content);
                if (!contentIsBlank && (vector is null || vector.Length == 0))
                {
                    _logger.LogWarning(
                        "Batch embedding returned an unusable vector for message {MessageId}; replaying it individually.",
                        messageList[messageIndex].MessageId);
                    vector = await _embeddingOrchestrator
                        .EmbedMessageAsync(messageList[messageIndex].Content, cancellationToken)
                        .ConfigureAwait(false);

                    if (vector is null || vector.Length == 0)
                    {
                        throw new InvalidOperationException(
                            $"Embedding for message {messageList[messageIndex].MessageId} was empty after an " +
                            "individual replay, although its content is not blank; refusing to persist a " +
                            "message that could never be retrieved.");
                    }
                }

                messageList[messageIndex] = messageList[messageIndex] with
                {
                    Embedding = vector,
                };
            }
        }

        _logger.LogDebug("Batch adding {Count} messages", messageList.Count);
        return await _messageRepo.AddBatchAsync(messageList, cancellationToken).ConfigureAwait(false);
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
        return await _messageRepo.GetRecentBySessionAsync(sessionId, cappedLimit, cancellationToken).ConfigureAwait(false);
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
        var scored = await SearchMessagesWithScoresAsync(
            sessionId, queryEmbedding, limit, minScore, cancellationToken).ConfigureAwait(false);
        return scored.Select(result => result.Message).ToList();
    }

    /// <summary>
    /// Returns the repository's existing ranked message results without a second query. This internal
    /// contract is used only when a recall explicitly requests diagnostics; the public short-term service
    /// remains source-compatible for custom implementations.
    /// </summary>
    public Task<IReadOnlyList<(Message Message, double Score)>> SearchMessagesWithScoresAsync(
        string? sessionId,
        float[] queryEmbedding,
        int limit,
        double minScore,
        CancellationToken cancellationToken) =>
        _messageRepo.SearchByVectorAsync(
            queryEmbedding, sessionId, limit, minScore, null, cancellationToken);

    /// <inheritdoc/>
    public async Task ClearSessionAsync(
        string sessionId,
        string? ownerId = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Clearing session {SessionId}, owner={Owner}", sessionId, ownerId);
        await _messageRepo.DeleteBySessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        await _conversationRepo.DeleteBySessionAsync(sessionId, cancellationToken).ConfigureAwait(false);
        // ReasoningTrace carries owner_id (R1): confine the delete to the calling owner's bucket so a
        // shared session_id can't let one owner's clear evict another owner's traces.
        await _reasoningTraceRepo.DeleteBySessionAsync(sessionId, ownerId, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Message>> GetRecentMessagesAsOfAsync(
        string sessionId,
        DateTimeOffset asOf,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var cappedLimit = Math.Min(limit, _options.MaxMessagesPerQuery);
        return await _messageRepo.GetRecentBySessionAsOfAsync(sessionId, asOf, cappedLimit, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// Internal scored-search capability implemented by the built-in short-term memory service. Keeping this
/// separate from <see cref="IShortTermMemoryService"/> avoids a breaking interface addition for providers.
/// </summary>
internal interface IScoredMessageSearch
{
    Task<IReadOnlyList<(Message Message, double Score)>> SearchMessagesWithScoresAsync(
        string? sessionId,
        float[] queryEmbedding,
        int limit,
        double minScore,
        CancellationToken cancellationToken);
}
