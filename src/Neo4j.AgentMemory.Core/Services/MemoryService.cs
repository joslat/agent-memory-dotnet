using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Facade service for all memory operations.
/// </summary>
public sealed class MemoryService : IMemoryService
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly IMemoryContextAssembler _assembler;
    private readonly IMemoryExtractionPipeline _extraction;
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly MemoryOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<MemoryService> _logger;

    public MemoryService(
        IShortTermMemoryService shortTerm,
        IMemoryContextAssembler assembler,
        IMemoryExtractionPipeline extraction,
        IEntityRepository entityRepository,
        IFactRepository factRepository,
        IPreferenceRepository preferenceRepository,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IOptions<MemoryOptions> options,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<MemoryService> logger)
    {
        _shortTerm = shortTerm;
        _assembler = assembler;
        _extraction = extraction;
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _preferenceRepository = preferenceRepository;
        _embeddingGenerator = embeddingGenerator;
        _options = options.Value;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    public async Task<RecallResult> RecallAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Recalling memory for session {SessionId}", request.SessionId);
        var context = await _assembler.AssembleContextAsync(request, cancellationToken);

        int totalItems = context.RecentMessages.Items.Count
            + context.RelevantMessages.Items.Count
            + context.RelevantEntities.Items.Count
            + context.RelevantPreferences.Items.Count
            + context.RelevantFacts.Items.Count
            + context.SimilarTraces.Items.Count;

        int estimatedChars =
            context.RecentMessages.Items.Sum(m => m.Content.Length)
            + context.RelevantMessages.Items.Sum(m => m.Content.Length)
            + context.RelevantEntities.Items.Sum(e => (e.Name?.Length ?? 0) + (e.Description?.Length ?? 0))
            + context.RelevantPreferences.Items.Sum(p => p.PreferenceText.Length)
            + context.RelevantFacts.Items.Sum(f => f.Subject.Length + f.Predicate.Length + f.Object.Length)
            + context.SimilarTraces.Items.Sum(t => t.Task.Length)
            + (context.GraphRagContext?.Length ?? 0);

        var budget = _options.ContextBudget;
        int? estimatedTokens = null;
        if (budget.MaxTokens.HasValue)
            estimatedTokens = estimatedChars / 4;

        return new RecallResult
        {
            Context = context,
            TotalItemsRetrieved = totalItems,
            EstimatedTokenCount = estimatedTokens
        };
    }

    public async Task<Message> AddMessageAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        var message = new Message
        {
            MessageId = _idGenerator.GenerateId(),
            SessionId = sessionId,
            ConversationId = conversationId,
            Role = role,
            Content = content,
            TimestampUtc = _clock.UtcNow,
            Metadata = metadata ?? new Dictionary<string, object>()
        };

        return await _shortTerm.AddMessageAsync(message, cancellationToken);
    }

    public Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default)
    {
        return _shortTerm.AddMessagesAsync(messages, cancellationToken);
    }

    public Task<ExtractionResult> ExtractAndPersistAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Extracting and persisting memory for session {SessionId}", request.SessionId);
        return _extraction.ExtractAsync(request, cancellationToken);
    }

    public Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Clearing session {SessionId}", sessionId);
        return _shortTerm.ClearSessionAsync(sessionId, cancellationToken);
    }

    public async Task ExtractFromSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Retroactive extraction for session {SessionId}", sessionId);

        var messages = await _shortTerm.GetRecentMessagesAsync(sessionId, int.MaxValue, cancellationToken);
        if (messages.Count == 0)
        {
            _logger.LogDebug("No messages found for session {SessionId} — skipping extraction.", sessionId);
            return;
        }

        await _extraction.ExtractAsync(
            new ExtractionRequest { Messages = messages, SessionId = sessionId },
            cancellationToken);
    }

    public async Task ExtractFromConversationAsync(
        string conversationId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Retroactive extraction for conversation {ConversationId}", conversationId);

        var messages = await _shortTerm.GetConversationMessagesAsync(conversationId, cancellationToken);
        if (messages.Count == 0)
        {
            _logger.LogDebug("No messages found for conversation {ConversationId} — skipping extraction.", conversationId);
            return;
        }

        var sessionId = messages[0].SessionId;
        await _extraction.ExtractAsync(
            new ExtractionRequest { Messages = messages, SessionId = sessionId },
            cancellationToken);
    }

    public async Task<int> GenerateEmbeddingsBatchAsync(
        string nodeLabel,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Batch embedding generation for label '{NodeLabel}', batchSize={BatchSize}", nodeLabel, batchSize);

        return nodeLabel switch
        {
            "Entity"     => await BackfillEntityEmbeddingsAsync(batchSize, cancellationToken),
            "Fact"       => await BackfillFactEmbeddingsAsync(batchSize, cancellationToken),
            "Preference" => await BackfillPreferenceEmbeddingsAsync(batchSize, cancellationToken),
            _ => throw new ArgumentException(
                $"Unsupported node label '{nodeLabel}'. Supported values: Entity, Fact, Preference.",
                nameof(nodeLabel))
        };
    }

    private async Task<int> BackfillEntityEmbeddingsAsync(int batchSize, CancellationToken ct)
    {
        int total = 0;
        IReadOnlyList<Entity> page;
        do
        {
            page = await _entityRepository.GetPageWithoutEmbeddingAsync(batchSize, ct);
            foreach (var entity in page)
            {
                var generated = await _embeddingGenerator.GenerateAsync([entity.Name], cancellationToken: ct);
                await _entityRepository.UpdateEmbeddingAsync(entity.EntityId, generated[0].Vector.ToArray(), ct);
                total++;
            }
        } while (page.Count == batchSize);

        _logger.LogInformation("Back-filled embeddings for {Count} Entity nodes.", total);
        return total;
    }

    private async Task<int> BackfillFactEmbeddingsAsync(int batchSize, CancellationToken ct)
    {
        int total = 0;
        IReadOnlyList<Fact> page;
        do
        {
            page = await _factRepository.GetPageWithoutEmbeddingAsync(batchSize, ct);
            foreach (var fact in page)
            {
                var text = $"{fact.Subject} {fact.Predicate} {fact.Object}";
                var generated = await _embeddingGenerator.GenerateAsync([text], cancellationToken: ct);
                await _factRepository.UpdateEmbeddingAsync(fact.FactId, generated[0].Vector.ToArray(), ct);
                total++;
            }
        } while (page.Count == batchSize);

        _logger.LogInformation("Back-filled embeddings for {Count} Fact nodes.", total);
        return total;
    }

    private async Task<int> BackfillPreferenceEmbeddingsAsync(int batchSize, CancellationToken ct)
    {
        int total = 0;
        IReadOnlyList<Preference> page;
        do
        {
            page = await _preferenceRepository.GetPageWithoutEmbeddingAsync(batchSize, ct);
            foreach (var pref in page)
            {
                var generated = await _embeddingGenerator.GenerateAsync([pref.PreferenceText], cancellationToken: ct);
                await _preferenceRepository.UpdateEmbeddingAsync(pref.PreferenceId, generated[0].Vector.ToArray(), ct);
                total++;
            }
        } while (page.Count == batchSize);

        _logger.LogInformation("Back-filled embeddings for {Count} Preference nodes.", total);
        return total;
    }
}
