using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Service for short-term (conversational) memory operations.
/// </summary>
public sealed class ShortTermMemoryService : IShortTermMemoryService
{
    private readonly IConversationRepository _conversationRepo;
    private readonly IMessageRepository _messageRepo;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ShortTermMemoryOptions _options;
    private readonly ILogger<ShortTermMemoryService> _logger;

    public ShortTermMemoryService(
        IConversationRepository conversationRepo,
        IMessageRepository messageRepo,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IClock clock,
        IIdGenerator idGenerator,
        IOptions<ShortTermMemoryOptions> options,
        ILogger<ShortTermMemoryService> logger)
    {
        _conversationRepo = conversationRepo;
        _messageRepo = messageRepo;
        _embeddingGenerator = embeddingGenerator;
        _clock = clock;
        _idGenerator = idGenerator;
        _options = options.Value;
        _logger = logger;
    }

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

    public async Task<Message> AddMessageAsync(
        Message message,
        CancellationToken cancellationToken = default)
    {
        var finalMessage = message;

        if (_options.GenerateEmbeddings && message.Embedding is null)
        {
            _logger.LogDebug("Generating embedding for message {MessageId}", message.MessageId);
            var generated = await _embeddingGenerator.GenerateAsync([message.Content], cancellationToken: cancellationToken);
            finalMessage = message with { Embedding = generated[0].Vector.ToArray() };
        }

        return await _messageRepo.AddAsync(finalMessage, cancellationToken);
    }

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
                var generated = await _embeddingGenerator.GenerateAsync([message.Content], cancellationToken: cancellationToken);
                finalMessage = message with { Embedding = generated[0].Vector.ToArray() };
            }
            results.Add(finalMessage);
        }

        _logger.LogDebug("Batch adding {Count} messages", results.Count);
        return await _messageRepo.AddBatchAsync(results, cancellationToken);
    }

    public async Task<IReadOnlyList<Message>> GetRecentMessagesAsync(
        string sessionId,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        var cappedLimit = Math.Min(limit, _options.MaxMessagesPerQuery);
        return await _messageRepo.GetRecentBySessionAsync(sessionId, cappedLimit, cancellationToken);
    }

    public Task<IReadOnlyList<Message>> GetConversationMessagesAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        return _messageRepo.GetByConversationAsync(conversationId, cancellationToken);
    }

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

    public async Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Clearing session {SessionId}", sessionId);
        await _messageRepo.DeleteBySessionAsync(sessionId, cancellationToken);

        var conversations = await _conversationRepo.GetBySessionAsync(sessionId, cancellationToken);
        foreach (var conversation in conversations)
            await _conversationRepo.DeleteAsync(conversation.ConversationId, cancellationToken);
    }
}
