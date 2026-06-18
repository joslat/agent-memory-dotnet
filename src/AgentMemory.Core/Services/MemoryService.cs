using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Services;

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
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IMemoryDecayService? _decayService;
    private readonly IConversationRepository? _conversationRepository;
    private readonly MemoryOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<MemoryService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryService"/> class.
    /// </summary>
    public MemoryService(
        IShortTermMemoryService shortTerm,
        IMemoryContextAssembler assembler,
        IMemoryExtractionPipeline extraction,
        IEntityRepository entityRepository,
        IFactRepository factRepository,
        IPreferenceRepository preferenceRepository,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IOptions<MemoryOptions> options,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<MemoryService> logger,
        IMemoryDecayService? decayService = null,
        IConversationRepository? conversationRepository = null)
    {
        ArgumentNullException.ThrowIfNull(shortTerm);
        ArgumentNullException.ThrowIfNull(assembler);
        ArgumentNullException.ThrowIfNull(extraction);
        ArgumentNullException.ThrowIfNull(entityRepository);
        ArgumentNullException.ThrowIfNull(factRepository);
        ArgumentNullException.ThrowIfNull(preferenceRepository);
        ArgumentNullException.ThrowIfNull(embeddingOrchestrator);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(idGenerator);
        ArgumentNullException.ThrowIfNull(logger);

        _shortTerm = shortTerm;
        _assembler = assembler;
        _extraction = extraction;
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _preferenceRepository = preferenceRepository;
        _embeddingOrchestrator = embeddingOrchestrator;
        _options = options.Value;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
        _decayService = decayService;
        _conversationRepository = conversationRepository;
    }

    /// <inheritdoc/>
    public async Task<RecallResult> RecallAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug("Recalling memory for session {SessionId}", request.SessionId);
        var context = await _assembler.AssembleContextAsync(request, cancellationToken);

        // Update access timestamps for recalled long-term memories (awaited so failures and
        // cancellation are observed; the method itself is resilient and logs internally).
        if (_decayService is not null)
        {
            await UpdateAccessTimestampsAsync(context, cancellationToken);
        }

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
            EstimatedTokenCount = estimatedTokens,
            Truncated = context.Truncated
        };
    }

    /// <inheritdoc/>
    public Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
        // Single-clock recall == bitemporal recall with both clocks equal (D6).
        => RecallAsOfAsync(request, asOf, asOf, cancellationToken);

    /// <inheritdoc/>
    public async Task<RecallResult> RecallAsOfAsync(
        RecallRequest request,
        DateTimeOffset validAsOf,
        DateTimeOffset systemAsOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "Recalling memory for session {SessionId} validAsOf {ValidAsOf} systemAsOf {SystemAsOf}",
            request.SessionId, validAsOf, systemAsOf);
        var context = await _assembler.AssembleContextAsOfAsync(request, validAsOf, systemAsOf, cancellationToken);

        int totalItems = context.RecentMessages.Items.Count
            + context.RelevantEntities.Items.Count
            + context.RelevantPreferences.Items.Count
            + context.RelevantFacts.Items.Count;

        return new RecallResult
        {
            Context = context,
            TotalItemsRetrieved = totalItems,
            Truncated = context.Truncated,
            // "asOf" retained as the valid-time alias for backward compatibility; both clocks recorded.
            Metadata = new Dictionary<string, object>
            {
                ["asOf"] = validAsOf,
                ["validAsOf"] = validAsOf,
                ["systemAsOf"] = systemAsOf
            }
        };
    }

    /// <inheritdoc/>
    public async Task<Message> AddMessageAsync(
        string sessionId,
        string conversationId,
        string role,
        string content,
        IReadOnlyDictionary<string, object>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        ArgumentNullException.ThrowIfNull(content);

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

    /// <inheritdoc/>
    public Task<IReadOnlyList<Message>> AddMessagesAsync(
        IEnumerable<Message> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return _shortTerm.AddMessagesAsync(messages, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<ExtractionResult> ExtractAndPersistAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug("Extracting and persisting memory for session {SessionId}", request.SessionId);
        return _extraction.ExtractAsync(request, cancellationToken);
    }

    /// <inheritdoc/>
    public Task ClearSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _logger.LogDebug("Clearing session {SessionId}", sessionId);
        return _shortTerm.ClearSessionAsync(sessionId, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExtractFromSessionAsync(
        string sessionId,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _logger.LogDebug("Retroactive extraction for session {SessionId}, owner={Owner}", sessionId, userId);

        // Must use the uncapped, chronological session fetch: routing this through GetRecentMessagesAsync
        // would silently clamp to MaxMessagesPerQuery (default 100) and drop the oldest messages of a long
        // session, so the bulk of its knowledge would never be extracted.
        var messages = await _shortTerm.GetAllSessionMessagesAsync(sessionId, cancellationToken);
        if (messages.Count == 0)
        {
            _logger.LogDebug("No messages found for session {SessionId} — skipping extraction.", sessionId);
            return;
        }

        await _extraction.ExtractAsync(
            new ExtractionRequest { Messages = messages, SessionId = sessionId, UserId = userId },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task ExtractFromConversationAsync(
        string conversationId,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        _logger.LogDebug("Retroactive extraction for conversation {ConversationId}, owner={Owner}", conversationId, userId);

        var messages = await _shortTerm.GetConversationMessagesAsync(conversationId, cancellationToken);
        if (messages.Count == 0)
        {
            _logger.LogDebug("No messages found for conversation {ConversationId} — skipping extraction.", conversationId);
            return;
        }

        // R1: when the caller doesn't supply an owner, derive it from the conversation's stored owner
        // (Conversation.UserId) so retroactive extraction of an owned conversation is owner-stamped
        // rather than persisted as shared/global. Requires the conversation repository to be available;
        // when it isn't (graph-less/test setups), fall back to the explicit userId (or null = shared).
        var ownerId = userId;
        if (string.IsNullOrEmpty(ownerId) && _conversationRepository is not null)
        {
            var conversation = await _conversationRepository.GetByIdAsync(conversationId, cancellationToken);
            ownerId = conversation?.UserId;
        }

        var sessionId = messages[0].SessionId;
        await _extraction.ExtractAsync(
            new ExtractionRequest { Messages = messages, SessionId = sessionId, UserId = ownerId },
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<int> GenerateEmbeddingsBatchAsync(
        string nodeLabel,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeLabel);
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
        PagedResult<Entity> page;
        do
        {
            page = await _entityRepository.GetPageWithoutEmbeddingAsync(batchSize, ct);
            foreach (var entity in page.Items)
            {
                var embedding = await _embeddingOrchestrator.EmbedEntityAsync(entity.Name, ct);
                await _entityRepository.UpdateEmbeddingAsync(entity.EntityId, embedding, ct);
                total++;
            }
        } while (page.HasNextPage);

        _logger.LogInformation("Back-filled embeddings for {Count} Entity nodes.", total);
        return total;
    }

    private async Task<int> BackfillFactEmbeddingsAsync(int batchSize, CancellationToken ct)
    {
        int total = 0;
        PagedResult<Fact> page;
        do
        {
            page = await _factRepository.GetPageWithoutEmbeddingAsync(batchSize, ct);
            foreach (var fact in page.Items)
            {
                var embedding = await _embeddingOrchestrator.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, ct);
                await _factRepository.UpdateEmbeddingAsync(fact.FactId, embedding, ct);
                total++;
            }
        } while (page.HasNextPage);

        _logger.LogInformation("Back-filled embeddings for {Count} Fact nodes.", total);
        return total;
    }

    private async Task<int> BackfillPreferenceEmbeddingsAsync(int batchSize, CancellationToken ct)
    {
        int total = 0;
        PagedResult<Preference> page;
        do
        {
            page = await _preferenceRepository.GetPageWithoutEmbeddingAsync(batchSize, ct);
            foreach (var pref in page.Items)
            {
                var embedding = await _embeddingOrchestrator.EmbedPreferenceAsync(pref.PreferenceText, ct);
                await _preferenceRepository.UpdateEmbeddingAsync(pref.PreferenceId, embedding, ct);
                total++;
            }
        } while (page.HasNextPage);

        _logger.LogInformation("Back-filled embeddings for {Count} Preference nodes.", total);
        return total;
    }

    private async Task UpdateAccessTimestampsAsync(MemoryContext context, CancellationToken cancellationToken)
    {
        try
        {
            var tasks = new List<Task>();

            foreach (var entity in context.RelevantEntities.Items)
                tasks.Add(_decayService!.UpdateAccessTimestampAsync(entity.EntityId, "Entity", cancellationToken));

            foreach (var fact in context.RelevantFacts.Items)
                tasks.Add(_decayService!.UpdateAccessTimestampAsync(fact.FactId, "Fact", cancellationToken));

            foreach (var pref in context.RelevantPreferences.Items)
                tasks.Add(_decayService!.UpdateAccessTimestampAsync(pref.PreferenceId, "Preference", cancellationToken));

            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update access timestamps for recalled memories");
        }
    }
}
