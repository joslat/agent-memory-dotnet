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
internal sealed class MemoryService : IMemoryService
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
        var context = await _assembler.AssembleContextAsync(request, cancellationToken).ConfigureAwait(false);

        // Update access timestamps for recalled long-term memories (awaited so failures and
        // cancellation are observed; the method itself is resilient and logs internally).
        if (_decayService is not null)
        {
            await UpdateAccessTimestampsAsync(context, cancellationToken).ConfigureAwait(false);
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
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default)
        // Single-clock recall == bitemporal recall with both clocks equal (D6): default systemAsOf to asOf.
        => RecallAsOfCoreAsync(request, asOf, systemAsOf ?? asOf, cancellationToken);

    private async Task<RecallResult> RecallAsOfCoreAsync(
        RecallRequest request,
        DateTimeOffset validAsOf,
        DateTimeOffset systemAsOf,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        _logger.LogDebug(
            "Recalling memory for session {SessionId} validAsOf {ValidAsOf} systemAsOf {SystemAsOf}",
            request.SessionId, validAsOf, systemAsOf);
        var context = await _assembler.AssembleContextAsOfAsync(request, validAsOf, systemAsOf, cancellationToken).ConfigureAwait(false);

        // Count every populated section so TotalItemsRetrieved matches the documented "across all sections"
        // contract and the live RecallAsync path. SimilarTraces is populated on the as-of path too, so it
        // must be included (RelevantMessages is intentionally Empty here — see the assembler's as-of path).
        int totalItems = context.RecentMessages.Items.Count
            + context.RelevantEntities.Items.Count
            + context.RelevantPreferences.Items.Count
            + context.RelevantFacts.Items.Count
            + context.SimilarTraces.Items.Count;

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

        return await _shortTerm.AddMessageAsync(message, cancellationToken).ConfigureAwait(false);
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
        string? ownerId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        _logger.LogDebug("Clearing session {SessionId}, owner={Owner}", sessionId, ownerId);
        return _shortTerm.ClearSessionAsync(sessionId, ownerId, cancellationToken);
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
        var messages = await _shortTerm.GetAllSessionMessagesAsync(sessionId, cancellationToken).ConfigureAwait(false);
        if (messages.Count == 0)
        {
            _logger.LogDebug("No messages found for session {SessionId} — skipping extraction.", sessionId);
            return;
        }

        await _extraction.ExtractAsync(
            new ExtractionRequest { Messages = messages, SessionId = sessionId, UserId = userId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task ExtractFromConversationAsync(
        string conversationId,
        string? userId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(conversationId);
        _logger.LogDebug("Retroactive extraction for conversation {ConversationId}, owner={Owner}", conversationId, userId);

        var messages = await _shortTerm.GetConversationMessagesAsync(conversationId, cancellationToken).ConfigureAwait(false);
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
            var conversation = await _conversationRepository.GetByIdAsync(conversationId, cancellationToken).ConfigureAwait(false);
            ownerId = conversation?.UserId;
        }

        var sessionId = messages[0].SessionId;
        await _extraction.ExtractAsync(
            new ExtractionRequest { Messages = messages, SessionId = sessionId, UserId = ownerId },
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<int> GenerateEmbeddingsBatchAsync(
        MemoryNodeKind nodeKind,
        int batchSize = 100,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Batch embedding generation for {NodeKind}, batchSize={BatchSize}", nodeKind, batchSize);

        return nodeKind switch
        {
            MemoryNodeKind.Entity     => await BackfillEntityEmbeddingsAsync(batchSize, cancellationToken).ConfigureAwait(false),
            MemoryNodeKind.Fact       => await BackfillFactEmbeddingsAsync(batchSize, cancellationToken).ConfigureAwait(false),
            MemoryNodeKind.Preference => await BackfillPreferenceEmbeddingsAsync(batchSize, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(nodeKind), nodeKind, "Unknown MemoryNodeKind.")
        };
    }

    private async Task<int> BackfillEntityEmbeddingsAsync(int batchSize, CancellationToken cancellationToken)
    {
        int total = 0;
        PagedResult<Entity> page;
        do
        {
            page = await _entityRepository.GetPageWithoutEmbeddingAsync(batchSize, cancellationToken).ConfigureAwait(false);
            int embeddedThisPage = 0;
            foreach (var entity in page.Items)
            {
                var embedding = await _embeddingOrchestrator.EmbedEntityAsync(entity.Name, cancellationToken).ConfigureAwait(false);
                await _entityRepository.UpdateEmbeddingAsync(entity.EntityId, embedding, cancellationToken).ConfigureAwait(false);
                // Count only nodes actually updated: the repo skips persisting an empty (degraded) embedding,
                // so a skipped node must not inflate the "nodes updated" return value.
                if (embedding.Length > 0) { total++; embeddedThisPage++; }
            }

            if (StalledOnPage(page.Items.Count, embeddedThisPage, "Entity")) break;
        } while (page.HasNextPage);

        _logger.LogInformation("Back-filled embeddings for {Count} Entity nodes.", total);
        return total;
    }

    private async Task<int> BackfillFactEmbeddingsAsync(int batchSize, CancellationToken cancellationToken)
    {
        int total = 0;
        PagedResult<Fact> page;
        do
        {
            page = await _factRepository.GetPageWithoutEmbeddingAsync(batchSize, cancellationToken).ConfigureAwait(false);
            int embeddedThisPage = 0;
            foreach (var fact in page.Items)
            {
                var embedding = await _embeddingOrchestrator.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, cancellationToken).ConfigureAwait(false);
                await _factRepository.UpdateEmbeddingAsync(fact.FactId, embedding, cancellationToken).ConfigureAwait(false);
                // Count only nodes actually updated (the repo skips persisting an empty/degraded embedding).
                if (embedding.Length > 0) { total++; embeddedThisPage++; }
            }

            if (StalledOnPage(page.Items.Count, embeddedThisPage, "Fact")) break;
        } while (page.HasNextPage);

        _logger.LogInformation("Back-filled embeddings for {Count} Fact nodes.", total);
        return total;
    }

    private async Task<int> BackfillPreferenceEmbeddingsAsync(int batchSize, CancellationToken cancellationToken)
    {
        int total = 0;
        PagedResult<Preference> page;
        do
        {
            page = await _preferenceRepository.GetPageWithoutEmbeddingAsync(batchSize, cancellationToken).ConfigureAwait(false);
            int embeddedThisPage = 0;
            foreach (var pref in page.Items)
            {
                var embedding = await _embeddingOrchestrator.EmbedPreferenceAsync(pref.PreferenceText, cancellationToken).ConfigureAwait(false);
                await _preferenceRepository.UpdateEmbeddingAsync(pref.PreferenceId, embedding, cancellationToken).ConfigureAwait(false);
                // Count only nodes actually updated (the repo skips persisting an empty/degraded embedding).
                if (embedding.Length > 0) { total++; embeddedThisPage++; }
            }

            if (StalledOnPage(page.Items.Count, embeddedThisPage, "Preference")) break;
        } while (page.HasNextPage);

        _logger.LogInformation("Back-filled embeddings for {Count} Preference nodes.", total);
        return total;
    }

    // Forward-progress guard for the predicate-paged backfill loops (GetPageWithoutEmbedding has no cursor:
    // it re-selects `WHERE embedding IS NULL LIMIT n`). A node only leaves that set once it acquires a
    // non-empty embedding; the orchestrator degrades to an EMPTY vector on generation failure and the repo
    // deliberately skips writing an empty embedding (keeping the node re-queueable). So if a page returns
    // nodes but NONE were embedded, the remaining null nodes are un-embeddable this run (bad key / dimension
    // mismatch / provider outage) — stop instead of looping forever on the identical page.
    private bool StalledOnPage(int pageItemCount, int embeddedThisPage, string label)
    {
        if (pageItemCount > 0 && embeddedThisPage == 0)
        {
            _logger.LogWarning(
                "Embedding back-fill for {Label} made no progress on a page of {Count} node(s) — embedding " +
                "generation is failing; stopping the back-fill to avoid an unbounded retry loop.",
                label, pageItemCount);
            return true;
        }
        return false;
    }

    private async Task UpdateAccessTimestampsAsync(MemoryContext context, CancellationToken cancellationToken)
    {
        try
        {
            var tasks = new List<Task>();

            foreach (var entity in context.RelevantEntities.Items)
                tasks.Add(_decayService!.UpdateAccessTimestampAsync(entity.EntityId, MemoryNodeKind.Entity, cancellationToken));

            foreach (var fact in context.RelevantFacts.Items)
                tasks.Add(_decayService!.UpdateAccessTimestampAsync(fact.FactId, MemoryNodeKind.Fact, cancellationToken));

            foreach (var pref in context.RelevantPreferences.Items)
                tasks.Add(_decayService!.UpdateAccessTimestampAsync(pref.PreferenceId, MemoryNodeKind.Preference, cancellationToken));

            await Task.WhenAll(tasks).ConfigureAwait(false);
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
