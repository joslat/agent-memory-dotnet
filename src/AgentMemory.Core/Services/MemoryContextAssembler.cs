using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services.Budgeting;

namespace AgentMemory.Core.Services;

/// <summary>
/// Assembles memory context from multiple memory layers for a recall request.
/// </summary>
public sealed class MemoryContextAssembler : IMemoryContextAssembler
{
    private readonly IShortTermMemoryService _shortTerm;
    private readonly ILongTermMemoryService _longTerm;
    private readonly IReasoningMemoryService _reasoning;
    private readonly IGraphRagContextSource? _graphRag;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly MemoryOptions _options;
    private readonly IWritableMemoryRankingContext? _rankingContext;
    private readonly IReadOnlyDictionary<TruncationStrategy, ITruncationStrategy> _truncationStrategies;
    private readonly ILogger<MemoryContextAssembler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryContextAssembler"/> class with the built-in
    /// context-budget truncation strategies.
    /// </summary>
    public MemoryContextAssembler(
        IShortTermMemoryService shortTerm,
        ILongTermMemoryService longTerm,
        IReasoningMemoryService reasoning,
        IGraphRagContextSource? graphRag,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IClock clock,
        IOptions<MemoryOptions> options,
        ILogger<MemoryContextAssembler> logger,
        IWritableMemoryRankingContext? rankingContext = null)
        : this(shortTerm, longTerm, reasoning, graphRag, embeddingOrchestrator, clock, options, logger,
            rankingContext, truncationStrategies: null)
    {
    }

    /// <summary>
    /// Initializes a new instance with a custom set of <see cref="ITruncationStrategy"/> implementations
    /// (the DI path). Any supplied strategy overrides the built-in default for its
    /// <see cref="ITruncationStrategy.Strategy"/> value; the four built-ins always remain present as a
    /// fallback (so <see cref="TruncationStrategy.OldestFirst"/> is guaranteed for the unknown-strategy
    /// default). <paramref name="truncationStrategies"/> null ⇒ built-ins only.
    /// </summary>
    internal MemoryContextAssembler(
        IShortTermMemoryService shortTerm,
        ILongTermMemoryService longTerm,
        IReasoningMemoryService reasoning,
        IGraphRagContextSource? graphRag,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IClock clock,
        IOptions<MemoryOptions> options,
        ILogger<MemoryContextAssembler> logger,
        IWritableMemoryRankingContext? rankingContext,
        IEnumerable<ITruncationStrategy>? truncationStrategies)
    {
        _shortTerm = shortTerm;
        _longTerm = longTerm;
        _reasoning = reasoning;
        _graphRag = graphRag;
        _embeddingOrchestrator = embeddingOrchestrator;
        _clock = clock;
        _options = options.Value;
        _rankingContext = rankingContext;
        _truncationStrategies = BuildStrategyMap(truncationStrategies);
        _logger = logger;
    }

    // Start from the four built-in strategies (so the OldestFirst fallback is always available even when
    // DI passes an empty enumerable), then let any injected strategy override the default for its key.
    private static IReadOnlyDictionary<TruncationStrategy, ITruncationStrategy> BuildStrategyMap(
        IEnumerable<ITruncationStrategy>? injected)
    {
        var map = new Dictionary<TruncationStrategy, ITruncationStrategy>
        {
            [TruncationStrategy.OldestFirst] = new OldestFirstTruncationStrategy(),
            [TruncationStrategy.LowestScoreFirst] = new LowestScoreFirstTruncationStrategy(),
            [TruncationStrategy.Proportional] = new ProportionalTruncationStrategy(),
            [TruncationStrategy.Fail] = new FailTruncationStrategy(),
        };

        if (injected is not null)
            foreach (var strategy in injected)
                map[strategy.Strategy] = strategy;

        return map;
    }

    /// <inheritdoc/>
    public async Task<MemoryContext> AssembleContextAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Assembling memory context for session {SessionId}", request.SessionId);

        var recallOpts = request.Options;
        var minScore = recallOpts.MinSimilarityScore;
        var blendMode = recallOpts.BlendMode;

        // Owner/user scope for long-term recall (R1): an explicit RecallOptions.Scope wins; otherwise
        // derive it from RecallRequest.UserId. Null ⇒ global recall (backward-compatible default).
        var scope = recallOpts.Scope
            ?? (string.IsNullOrEmpty(request.UserId) ? null : MemoryScope.For(request.UserId));

        // Blend policy (spec §5.5 / plan §12.5): decide which sources contribute to the context.
        //   MemoryOnly   → memory layers only; GraphRAG suppressed even when enabled.
        //   GraphRagOnly → GraphRAG only; memory layers (and query embedding) skipped.
        //   others       → both sources (Blended / MemoryThenGraphRag / GraphRagThenMemory).
        bool includeMemory = blendMode != RetrievalBlendMode.GraphRagOnly;
        bool graphRagAvailable = _graphRag != null && _options.EnableGraphRag;
        bool includeGraphRag = blendMode != RetrievalBlendMode.MemoryOnly && graphRagAvailable;

        if (blendMode == RetrievalBlendMode.GraphRagOnly && !graphRagAvailable)
        {
            _logger.LogWarning(
                "GraphRagOnly blend mode requested for session {SessionId} but GraphRAG is unavailable " +
                "(source not registered or EnableGraphRag=false); returning empty context.",
                request.SessionId);
        }

        // Start GraphRAG retrieval first so it overlaps memory retrieval when both are requested.
        Task<GraphRagContextResult?>? graphRagTask = includeGraphRag
            ? FetchGraphRagAsync(request, recallOpts, cancellationToken)
            : null;

        IReadOnlyList<Message> recentMessages = Array.Empty<Message>();
        IReadOnlyList<Message> relevantMessages = Array.Empty<Message>();
        IReadOnlyList<Entity> entities = Array.Empty<Entity>();
        IReadOnlyList<Preference> preferences = Array.Empty<Preference>();
        IReadOnlyList<Fact> facts = Array.Empty<Fact>();
        IReadOnlyList<ReasoningTrace> traces = Array.Empty<ReasoningTrace>();

        if (includeMemory)
        {
            // Generate embedding if not provided (only needed for memory-layer semantic search).
            var queryEmbedding = request.QueryEmbedding
                ?? await _embeddingOrchestrator.EmbedQueryAsync(request.Query, cancellationToken);

            // Vector searches require a non-empty embedding. A blank query (e.g. a history-only
            // recall via the chat-history provider) yields an empty embedding — skip the semantic
            // searches rather than issue zero-dimension vector queries (which the index rejects).
            bool hasEmbedding = queryEmbedding is { Length: > 0 };
            static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

            // Recent messages need no embedding; the rest are semantic and are gated on hasEmbedding.
            var recentTask = _shortTerm.GetRecentMessagesAsync(
                request.SessionId, recallOpts.MaxRecentMessages, cancellationToken);

            var relevantTask = hasEmbedding
                ? _shortTerm.SearchMessagesAsync(request.SessionId, queryEmbedding, recallOpts.MaxRelevantMessages, minScore, cancellationToken)
                : Empty<Message>();

            // D3 — apply the per-request query intent (latest/analog) as an ambient ranking override for
            // the long-term vector searches below. The long-term repositories read it synchronously while
            // each task is *created* (before their first await), so we reset it immediately after creating
            // them — there is no await in this region, so the override never leaks past this recall.
            bool overrideRanking = _rankingContext is not null && recallOpts.Intent != RankingIntent.Default;
            if (overrideRanking) _rankingContext!.Current = _options.Ranking.ForIntent(recallOpts.Intent);

            var entitiesTask = hasEmbedding
                ? _longTerm.SearchEntitiesAsync(queryEmbedding, recallOpts.MaxEntities, minScore, scope, cancellationToken)
                : Empty<Entity>();

            var preferencesTask = hasEmbedding
                ? _longTerm.SearchPreferencesAsync(queryEmbedding, recallOpts.MaxPreferences, minScore, scope, cancellationToken)
                : Empty<Preference>();

            var factsTask = hasEmbedding
                ? _longTerm.SearchFactsAsync(queryEmbedding, recallOpts.MaxFacts, minScore, scope, cancellationToken)
                : Empty<Fact>();

            var tracesTask = hasEmbedding
                ? _reasoning.SearchSimilarTracesAsync(queryEmbedding, null, recallOpts.MaxTraces, minScore, scope, cancellationToken)
                : Empty<ReasoningTrace>();

            if (overrideRanking) _rankingContext!.Current = null;

            await Task.WhenAll(
                recentTask, relevantTask, entitiesTask,
                preferencesTask, factsTask, tracesTask);

            recentMessages = await recentTask;
            relevantMessages = await relevantTask;
            entities = await entitiesTask;
            preferences = await preferencesTask;
            facts = await factsTask;
            traces = await tracesTask;
        }

        if (graphRagTask != null)
            await graphRagTask;

        string? graphRagContext = null;
        if (graphRagTask != null)
        {
            var graphRagResult = await graphRagTask;
            if (graphRagResult?.Items is { Count: > 0 } items)
                graphRagContext = string.Join("\n\n", items.Select(i => i.Text));
        }

        // Apply context budget if configured
        var budget = _options.ContextBudget;
        bool truncated = false;

        if (budget.MaxTokens.HasValue || budget.MaxCharacters.HasValue)
        {
            (recentMessages, relevantMessages, entities, preferences, facts, traces, graphRagContext, truncated) =
                ApplyBudget(budget, recentMessages, relevantMessages, entities, preferences, facts, traces, graphRagContext);
        }

        int estimatedChars = ContextBudgetEstimator.EstimateChars(recentMessages)
            + ContextBudgetEstimator.EstimateChars(relevantMessages)
            + ContextBudgetEstimator.EstimateChars(entities)
            + ContextBudgetEstimator.EstimateChars(preferences)
            + ContextBudgetEstimator.EstimateChars(facts)
            + ContextBudgetEstimator.EstimateChars(traces)
            + (graphRagContext?.Length ?? 0);

        var context = new MemoryContext
        {
            SessionId = request.SessionId,
            AssembledAtUtc = _clock.UtcNow,
            RecentMessages = new MemoryContextSection<Message> { Items = recentMessages },
            RelevantMessages = new MemoryContextSection<Message> { Items = relevantMessages },
            RelevantEntities = new MemoryContextSection<Entity> { Items = entities },
            RelevantPreferences = new MemoryContextSection<Preference> { Items = preferences },
            RelevantFacts = new MemoryContextSection<Fact> { Items = facts },
            SimilarTraces = new MemoryContextSection<ReasoningTrace> { Items = traces },
            GraphRagContext = graphRagContext,
            BlendMode = blendMode,
            Truncated = truncated
        };

        _logger.LogDebug(
            "Assembled context for session {SessionId}: {Chars} chars (~{Tokens} tokens), truncated={Truncated}",
            request.SessionId, estimatedChars, estimatedChars / 4, truncated);

        return context;
    }

    /// <inheritdoc/>
    public Task<MemoryContext> AssembleContextAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
        // Single-clock recall is the bitemporal recall with both clocks equal (D6): identical behaviour
        // to before — validAsOf == systemAsOf binds every filter to the same instant.
        => AssembleContextAsOfAsync(request, asOf, asOf, cancellationToken);

    /// <inheritdoc/>
    public async Task<MemoryContext> AssembleContextAsOfAsync(
        RecallRequest request,
        DateTimeOffset validAsOf,
        DateTimeOffset systemAsOf,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Assembling bitemporal memory context for session {SessionId} validAsOf {ValidAsOf} systemAsOf {SystemAsOf}",
            request.SessionId, validAsOf, systemAsOf);

        var recallOpts = request.Options;
        var minScore = recallOpts.MinSimilarityScore;

        // R1 (IC5): scope temporal recall to the requesting owner, identically to the live path.
        var scope = recallOpts.Scope
            ?? (string.IsNullOrEmpty(request.UserId) ? null : MemoryScope.For(request.UserId));

        var queryEmbedding = request.QueryEmbedding
            ?? await _embeddingOrchestrator.EmbedQueryAsync(request.Query, cancellationToken);

        // D6 clock mapping: the transaction clock ($systemAsOf) bounds every record's existence, so
        // messages, entities, preferences, and traces — which have no valid-time window — observe only
        // systemAsOf. Facts additionally observe the valid-time clock ($validAsOf) for their validity
        // window. When the clocks are equal (single-clock recall) this is byte-for-byte the old behaviour.
        var recentTask = _shortTerm.GetRecentMessagesAsOfAsync(
            request.SessionId, systemAsOf, recallOpts.MaxRecentMessages, cancellationToken);

        var entitiesTask = _longTerm.SearchEntitiesAsOfAsync(
            queryEmbedding, systemAsOf, recallOpts.MaxEntities, minScore, scope, cancellationToken);

        var preferencesTask = _longTerm.SearchPreferencesAsOfAsync(
            queryEmbedding, systemAsOf, recallOpts.MaxPreferences, minScore, scope, cancellationToken);

        var factsTask = _longTerm.SearchFactsAsOfAsync(
            queryEmbedding, validAsOf, recallOpts.MaxFacts, minScore, scope, systemAsOf, cancellationToken);

        var tracesTask = _reasoning.SearchSimilarTracesAsOfAsync(
            queryEmbedding, systemAsOf, null, recallOpts.MaxTraces, minScore, scope, cancellationToken);

        await Task.WhenAll(recentTask, entitiesTask, preferencesTask, factsTask, tracesTask);

        var recentMessages = await recentTask;
        var entities = await entitiesTask;
        var preferences = await preferencesTask;
        var facts = await factsTask;
        var traces = await tracesTask;

        // Enforce the same context budget as the live recall path so temporal recall cannot blow
        // past the configured token/char limit. (Relevant messages are not part of the temporal
        // snapshot, so they pass through as empty.)
        var budget = _options.ContextBudget;
        bool truncated = false;
        if (budget.MaxTokens.HasValue || budget.MaxCharacters.HasValue)
        {
            var fitted = ApplyBudget(
                budget, recentMessages, Array.Empty<Message>(), entities, preferences, facts,
                traces, graphRagContext: null);
            recentMessages = fitted.Recent;
            entities = fitted.Entities;
            preferences = fitted.Preferences;
            facts = fitted.Facts;
            traces = fitted.Traces;
            truncated = fitted.Truncated;
        }

        var context = new MemoryContext
        {
            SessionId = request.SessionId,
            AssembledAtUtc = _clock.UtcNow,
            RecentMessages = new MemoryContextSection<Message> { Items = recentMessages },
            RelevantMessages = MemoryContextSection<Message>.Empty,
            RelevantEntities = new MemoryContextSection<Entity> { Items = entities },
            RelevantPreferences = new MemoryContextSection<Preference> { Items = preferences },
            RelevantFacts = new MemoryContextSection<Fact> { Items = facts },
            SimilarTraces = new MemoryContextSection<ReasoningTrace> { Items = traces },
            Truncated = truncated,
            // "asOf" retained as the valid-time alias for backward compatibility; both clocks recorded.
            Metadata = new Dictionary<string, object>
            {
                ["asOf"] = validAsOf,
                ["validAsOf"] = validAsOf,
                ["systemAsOf"] = systemAsOf
            }
        };

        _logger.LogDebug(
            "Assembled bitemporal context for session {SessionId} validAsOf {ValidAsOf} systemAsOf {SystemAsOf}: {Entities} entities, {Facts} facts, {Prefs} preferences, {Traces} traces",
            request.SessionId, validAsOf, systemAsOf, entities.Count, facts.Count, preferences.Count, traces.Count);

        return context;
    }

    private async Task<GraphRagContextResult?> FetchGraphRagAsync(
        RecallRequest request,
        RecallOptions recallOpts,
        CancellationToken cancellationToken)
    {
        try
        {
            var graphRagRequest = new GraphRagContextRequest
            {
                SessionId = request.SessionId,
                UserId = request.UserId,
                Query = request.Query,
                TopK = recallOpts.MaxGraphRagItems
            };
            return await _graphRag!.GetContextAsync(graphRagRequest, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GraphRAG retrieval failed for session {SessionId}", request.SessionId);
            return null;
        }
    }

    private sealed record AssembledSections(
        IReadOnlyList<Message> Recent,
        IReadOnlyList<Message> Relevant,
        IReadOnlyList<Entity> Entities,
        IReadOnlyList<Preference> Preferences,
        IReadOnlyList<Fact> Facts,
        IReadOnlyList<ReasoningTrace> Traces,
        string? GraphRag,
        bool Truncated);

    private AssembledSections ApplyBudget(
        ContextBudget budget,
        IReadOnlyList<Message> recent,
        IReadOnlyList<Message> relevant,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<Preference> preferences,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<ReasoningTrace> traces,
        string? graphRagContext)
    {
        int maxChars = budget.MaxCharacters
            ?? (budget.MaxTokens.HasValue ? budget.MaxTokens.Value * 4 : int.MaxValue);

        int totalChars = ContextBudgetEstimator.EstimateChars(recent) + ContextBudgetEstimator.EstimateChars(relevant)
            + ContextBudgetEstimator.EstimateChars(entities) + ContextBudgetEstimator.EstimateChars(preferences)
            + ContextBudgetEstimator.EstimateChars(facts) + ContextBudgetEstimator.EstimateChars(traces)
            + (graphRagContext?.Length ?? 0);

        if (totalChars <= maxChars)
            return new AssembledSections(recent, relevant, entities, preferences, facts, traces, graphRagContext, false);

        var strategy = ResolveStrategy(budget.TruncationStrategy);
        var result = strategy.Truncate(new TruncationInput(
            maxChars, totalChars, recent, relevant, entities, preferences, facts, traces, graphRagContext));

        return new AssembledSections(
            result.Recent, result.Relevant, result.Entities, result.Preferences,
            result.Facts, result.Traces, result.GraphRag, Truncated: true);
    }

    // Dispatch to the requested strategy, falling back to OldestFirst for any value without a registered
    // strategy — preserving the original switch's default arm. OldestFirst is always present (BuildStrategyMap).
    private ITruncationStrategy ResolveStrategy(TruncationStrategy strategy) =>
        _truncationStrategies.TryGetValue(strategy, out var resolved)
            ? resolved
            : _truncationStrategies[TruncationStrategy.OldestFirst];
}
