using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services.Budgeting;

namespace AgentMemory.Core.Services;

/// <summary>
/// Assembles memory context from multiple memory layers for a recall request.
/// </summary>
internal sealed class MemoryContextAssembler : IMemoryContextAssembler
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
    private readonly IMemoryIsolationPolicy _isolationPolicy;

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
        IMemoryIsolationPolicy isolationPolicy,
        IWritableMemoryRankingContext? rankingContext = null)
        : this(shortTerm, longTerm, reasoning, graphRag, embeddingOrchestrator, clock, options, logger,
            isolationPolicy, rankingContext, truncationStrategies: null)
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
        IMemoryIsolationPolicy isolationPolicy,
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
        _isolationPolicy = isolationPolicy;
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

        // Owner/user scope for long-term recall (R1), resolved through the central isolation policy
        // (#100) instead of an inline fallback: SingleTenant reproduces the old "explicit scope wins,
        // else derive from UserId, else global" behavior exactly; WarnOnUnscoped/StrictMultiTenant add a
        // warning/fail-closed on top for a genuinely unscoped tenant recall.
        var scope = _isolationPolicy.ResolveReadScope(
            recallOpts.Scope, request.UserId, nameof(AssembleContextAsync), MemoryOperationAccess.Tenant);

        // Blend policy (spec §5.5 / plan §12.5): decide which sources contribute to the context.
        //   MemoryOnly   → memory layers only; GraphRAG suppressed even when enabled.
        //   GraphRagOnly → GraphRAG only; memory layers (and query embedding) skipped.
        //   others       → both sources (Blended / MemoryThenGraphRag / GraphRagThenMemory).
        bool includeMemory = blendMode != RetrievalBlendMode.GraphRagOnly;
        bool graphRagAvailable = _graphRag != null && _options.EnableGraphRag;
        // #88: a task-aware recall policy that excludes GraphRag from its decision zeroes MaxGraphRagItems
        // -- skip the (potentially expensive) GraphRAG round-trip entirely rather than call into it with
        // TopK=0 (which Neo4jGraphRagContextSource treats as "use the configured default TopK", NOT "return
        // nothing" -- an existing, unrelated quirk this skip sidesteps rather than depends on).
        bool includeGraphRag = blendMode != RetrievalBlendMode.MemoryOnly && graphRagAvailable
            && recallOpts.MaxGraphRagItems > 0;

        if (blendMode == RetrievalBlendMode.GraphRagOnly && !graphRagAvailable)
        {
            _logger.LogWarning(
                "GraphRagOnly blend mode requested for session {SessionId} but GraphRAG is unavailable " +
                "(source not registered or EnableGraphRag=false); returning empty context.",
                request.SessionId);
        }
        else if (blendMode == RetrievalBlendMode.GraphRagOnly && recallOpts.MaxGraphRagItems <= 0)
        {
            // #88: GraphRAG is registered/enabled (graphRagAvailable), but this turn's effective
            // MaxGraphRagItems is zero -- e.g. an automatic recall policy excluded the GraphRag category.
            // GraphRagOnly means nothing else is retrieved either, so without this warning a host combining
            // GraphRagOnly with such a policy would silently get a completely empty context every turn.
            _logger.LogWarning(
                "GraphRagOnly blend mode requested for session {SessionId} but MaxGraphRagItems is {MaxGraphRagItems} " +
                "(excluded by RecallOptions or an automatic recall policy decision); returning empty context.",
                request.SessionId, recallOpts.MaxGraphRagItems);
        }

        // Start GraphRAG retrieval first so it overlaps memory retrieval when both are requested.
        Task<GraphRagContextResult?>? graphRagTask = includeGraphRag
            ? FetchGraphRagAsync(request, recallOpts, scope, cancellationToken)
            : null;

        IReadOnlyList<Message> recentMessages = Array.Empty<Message>();
        IReadOnlyList<Message> relevantMessages = Array.Empty<Message>();
        IReadOnlyList<(Message Message, double Score)> relevantMessageScores =
            Array.Empty<(Message, double)>();
        IReadOnlyList<Entity> entities = Array.Empty<Entity>();
        IReadOnlyList<Preference> preferences = Array.Empty<Preference>();
        IReadOnlyList<Fact> facts = Array.Empty<Fact>();
        IReadOnlyList<ReasoningTrace> traces = Array.Empty<ReasoningTrace>();

        if (includeMemory)
        {
            // Generate embedding if not provided (only needed for memory-layer semantic search).
            var queryEmbedding = request.QueryEmbedding
                ?? await TimedAsync(
                        "memory.recall.embedding",
                        () => _embeddingOrchestrator.EmbedQueryAsync(request.Query, cancellationToken))
                    .ConfigureAwait(false);

            // Vector searches require a non-empty embedding. A blank query (e.g. a history-only
            // recall via the chat-history provider) yields an empty embedding — skip the semantic
            // searches rather than issue zero-dimension vector queries (which the index rejects).
            bool hasEmbedding = queryEmbedding is { Length: > 0 };
            static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

            // Recent messages need no embedding; the rest are semantic and are gated on hasEmbedding. Each
            // is also gated on its own MaxX > 0 (#88): a task-aware recall policy that excludes a category
            // zeroes its limit, and skipping the call entirely (rather than issuing a LIMIT/TopK 0 query
            // whose result is always empty) is what actually saves the embedding/database work the policy
            // is asking to avoid.
            // Each retrieval is wrapped in a per-category span so a measurement harness can attribute
            // recall time by category rather than seeing one opaque total. TimedAsync invokes its factory
            // synchronously (an async method runs to its first await, and that await IS the factory call),
            // which is what keeps the ranking-context contract below intact — the repositories still read
            // the ambient override while the task is *created*, exactly as they did before.
            var recentTask = recallOpts.MaxRecentMessages > 0
                ? TimedAsync("memory.recall.recent",
                    () => _shortTerm.GetRecentMessagesAsync(request.SessionId, recallOpts.MaxRecentMessages, cancellationToken))
                : Empty<Message>();

            var relevantTask = hasEmbedding && recallOpts.MaxRelevantMessages > 0
                ? TimedAsync("memory.recall.messages",
                    () => SearchRelevantMessagesAsync(
                        request.SessionId,
                        queryEmbedding,
                        recallOpts.MaxRelevantMessages,
                        minScore,
                        recallOpts.IncludeDiagnostics,
                        cancellationToken))
                : Task.FromResult(RelevantMessageSearchResult.Empty);

            // D3 — apply the per-request query intent (latest/analog) as an ambient ranking override for
            // the long-term vector searches below. The long-term repositories read it synchronously while
            // each task is *created* (before their first await), so we reset it immediately after creating
            // them — there is no await in this region, so the override never leaks past this recall.
            bool overrideRanking = _rankingContext is not null && recallOpts.Intent != RankingIntent.Default;
            if (overrideRanking) _rankingContext!.Current = _options.Ranking.ForIntent(recallOpts.Intent);

            var entitiesTask = hasEmbedding && recallOpts.MaxEntities > 0
                ? TimedAsync("memory.recall.entities",
                    () => _longTerm.SearchEntitiesAsync(queryEmbedding, recallOpts.MaxEntities, minScore, scope, cancellationToken))
                : Empty<Entity>();

            var preferencesTask = hasEmbedding && recallOpts.MaxPreferences > 0
                ? TimedAsync("memory.recall.preferences",
                    () => _longTerm.SearchPreferencesAsync(queryEmbedding, recallOpts.MaxPreferences, minScore, scope, cancellationToken))
                : Empty<Preference>();

            var factsTask = hasEmbedding && recallOpts.MaxFacts > 0
                ? TimedAsync("memory.recall.facts",
                    () => _longTerm.SearchFactsAsync(queryEmbedding, recallOpts.MaxFacts, minScore, scope, cancellationToken))
                : Empty<Fact>();

            var tracesTask = hasEmbedding && recallOpts.MaxTraces > 0
                ? TimedAsync("memory.recall.traces",
                    () => _reasoning.SearchSimilarTracesAsync(queryEmbedding, null, recallOpts.MaxTraces, minScore, scope, cancellationToken))
                : Empty<ReasoningTrace>();

            if (overrideRanking) _rankingContext!.Current = null;

            await Task.WhenAll(
                recentTask, relevantTask, entitiesTask,
                preferencesTask, factsTask, tracesTask).ConfigureAwait(false);

            recentMessages = await recentTask.ConfigureAwait(false);
            var relevantResult = await relevantTask.ConfigureAwait(false);
            relevantMessages = relevantResult.Messages;
            relevantMessageScores = relevantResult.ScoredMessages;
            entities = await entitiesTask.ConfigureAwait(false);
            preferences = await preferencesTask.ConfigureAwait(false);
            facts = await factsTask.ConfigureAwait(false);
            traces = await tracesTask.ConfigureAwait(false);
        }

        if (graphRagTask != null)
            await graphRagTask.ConfigureAwait(false);

        string? graphRagContext = null;
        if (graphRagTask != null)
        {
            var graphRagResult = await graphRagTask.ConfigureAwait(false);
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

        var rankedRelevantItems = recallOpts.IncludeDiagnostics
            ? BuildRankedItems(relevantMessages, relevantMessageScores)
            : Array.Empty<MemoryContextRankedItem>();

        var context = new MemoryContext
        {
            SessionId = request.SessionId,
            AssembledAtUtc = _clock.UtcNow,
            RecentMessages = new MemoryContextSection<Message> { Items = recentMessages },
            RelevantMessages = new MemoryContextSection<Message>
            {
                Items = relevantMessages,
                RankedItems = rankedRelevantItems
            },
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
        DateTimeOffset? systemAsOf = null,
        CancellationToken cancellationToken = default)
        // Single-clock recall is the bitemporal recall with both clocks equal (D6): default systemAsOf to
        // asOf — validAsOf == systemAsOf binds every filter to the same instant.
        => AssembleContextAsOfCoreAsync(request, asOf, systemAsOf ?? asOf, cancellationToken);

    private async Task<MemoryContext> AssembleContextAsOfCoreAsync(
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

        // R1 (IC5): scope temporal recall to the requesting owner, identically to the live path --
        // through the same central isolation policy (#100) as AssembleContextAsync.
        var scope = _isolationPolicy.ResolveReadScope(
            recallOpts.Scope, request.UserId, nameof(AssembleContextAsOfAsync), MemoryOperationAccess.Tenant);

        var queryEmbedding = request.QueryEmbedding
            ?? await _embeddingOrchestrator.EmbedQueryAsync(request.Query, cancellationToken).ConfigureAwait(false);

        // Vector searches require a non-empty embedding. A blank query, or a transient embedding-generation
        // failure (degrades to an empty vector), would otherwise issue a zero-dimension vector query which
        // the index REJECTS — throwing instead of returning empty. Mirror the live AssembleContextAsync
        // guard so temporal recall degrades gracefully to recent messages + empty semantic sections.
        bool hasEmbedding = queryEmbedding is { Length: > 0 };
        static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

        // D6 clock mapping: the transaction clock ($systemAsOf) bounds every record's existence, so
        // messages, entities, preferences, and traces — which have no valid-time window — observe only
        // systemAsOf. Facts additionally observe the valid-time clock ($validAsOf) for their validity
        // window. When the clocks are equal (single-clock recall) this is byte-for-byte the old behaviour.
        // Each is gated on its own MaxX > 0 (#88), same as the live AssembleContextAsync path: skip the
        // call entirely for a category a task-aware recall policy has excluded.
        var recentTask = recallOpts.MaxRecentMessages > 0
            ? _shortTerm.GetRecentMessagesAsOfAsync(request.SessionId, systemAsOf, recallOpts.MaxRecentMessages, cancellationToken)
            : Empty<Message>();

        var entitiesTask = hasEmbedding && recallOpts.MaxEntities > 0
            ? _longTerm.SearchEntitiesAsOfAsync(queryEmbedding, systemAsOf, recallOpts.MaxEntities, minScore, scope, cancellationToken)
            : Empty<Entity>();

        var preferencesTask = hasEmbedding && recallOpts.MaxPreferences > 0
            ? _longTerm.SearchPreferencesAsOfAsync(queryEmbedding, systemAsOf, recallOpts.MaxPreferences, minScore, scope, cancellationToken)
            : Empty<Preference>();

        var factsTask = hasEmbedding && recallOpts.MaxFacts > 0
            ? _longTerm.SearchFactsAsOfAsync(queryEmbedding, validAsOf, recallOpts.MaxFacts, minScore, scope, systemAsOf, cancellationToken)
            : Empty<Fact>();

        var tracesTask = hasEmbedding && recallOpts.MaxTraces > 0
            ? _reasoning.SearchSimilarTracesAsOfAsync(queryEmbedding, systemAsOf, null, recallOpts.MaxTraces, minScore, scope, cancellationToken)
            : Empty<ReasoningTrace>();

        await Task.WhenAll(recentTask, entitiesTask, preferencesTask, factsTask, tracesTask).ConfigureAwait(false);

        var recentMessages = await recentTask.ConfigureAwait(false);
        var entities = await entitiesTask.ConfigureAwait(false);
        var preferences = await preferencesTask.ConfigureAwait(false);
        var facts = await factsTask.ConfigureAwait(false);
        var traces = await tracesTask.ConfigureAwait(false);

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

    /// <summary>
    /// Runs <paramref name="factory"/> inside a named span so per-category recall cost is attributable.
    /// </summary>
    /// <remarks>
    /// The factory is invoked <em>synchronously</em> by the first <c>await</c> of this method, so wrapping
    /// a call here does not move it out of the caller's synchronous task-creation region. That matters at
    /// the call sites in <see cref="AssembleContextAsync"/>, where the long-term repositories read the
    /// ambient <see cref="IWritableMemoryRankingContext"/> override at task-creation time.
    /// When no listener is attached the span is null and this adds one null check plus one await.
    /// </remarks>
    private static async Task<T> TimedAsync<T>(string spanName, Func<Task<T>> factory)
    {
        using var activity = AgentMemoryDiagnostics.Source.StartActivity(spanName);
        return await factory().ConfigureAwait(false);
    }

    private async Task<GraphRagContextResult?> FetchGraphRagAsync(
        RecallRequest request,
        RecallOptions recallOpts,
        MemoryScope scope,
        CancellationToken cancellationToken)
    {
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.recall.graphrag");
        try
        {
            // #100 Stage 2: use the SAME already-resolved scope as every other recall source (line ~117),
            // not the raw request.UserId -- otherwise a caller scoping purely via RecallOptions.Scope (a
            // first-class, documented pattern; explicit scope wins over UserId) would have memory correctly
            // scoped to that owner while GraphRAG silently ran unscoped (a cross-owner leak outside strict
            // mode) or threw inconsistently (inside strict mode, since the pipeline already accepted this
            // recall as properly scoped).
            var graphRagRequest = new GraphRagContextRequest
            {
                SessionId = request.SessionId,
                UserId = scope.OwnerId,
                Query = request.Query,
                TopK = recallOpts.MaxGraphRagItems
            };
            return await _graphRag!.GetContextAsync(graphRagRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (MemoryOwnerScopeRequiredException)
        {
            // #100 Stage 2: GraphRAG is deliberately best-effort/resilient for genuine retrieval failures,
            // but a StrictMultiTenant isolation violation is not one of those -- it must propagate to the
            // caller, not be silently downgraded to "no GraphRAG context found" like a real outage would be.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GraphRAG retrieval failed for session {SessionId}", request.SessionId);
            return null;
        }
    }

    private async Task<RelevantMessageSearchResult> SearchRelevantMessagesAsync(
        string sessionId,
        float[] queryEmbedding,
        int limit,
        double minScore,
        bool includeDiagnostics,
        CancellationToken cancellationToken)
    {
        if (includeDiagnostics && _shortTerm is IScoredMessageSearch scoredSearch)
        {
            var scoredMessages = await scoredSearch.SearchMessagesWithScoresAsync(
                sessionId, queryEmbedding, limit, minScore, cancellationToken).ConfigureAwait(false);
            return new RelevantMessageSearchResult(
                scoredMessages.Select(result => result.Message).ToArray(),
                scoredMessages);
        }

        var messages = await _shortTerm.SearchMessagesAsync(
            sessionId, queryEmbedding, limit, minScore, cancellationToken).ConfigureAwait(false);
        return new RelevantMessageSearchResult(messages, Array.Empty<(Message, double)>());
    }

    internal static IReadOnlyList<MemoryContextRankedItem> BuildRankedItems(
        IReadOnlyList<Message> contextMessages,
        IReadOnlyList<(Message Message, double Score)> retrievedMessages)
    {
        if (contextMessages.Count == 0 || retrievedMessages.Count == 0)
            return Array.Empty<MemoryContextRankedItem>();

        var retrievedById = retrievedMessages
            .Select((result, index) => new
            {
                result.Message.MessageId,
                result.Score,
                RetrievalRank = index + 1
            })
            .ToDictionary(result => result.MessageId, StringComparer.Ordinal);

        var ranked = new List<MemoryContextRankedItem>(contextMessages.Count);
        for (var index = 0; index < contextMessages.Count; index++)
        {
            var message = contextMessages[index];
            if (!retrievedById.TryGetValue(message.MessageId, out var retrieved))
                continue;
            ranked.Add(new MemoryContextRankedItem(
                message.MessageId,
                retrieved.Score,
                retrieved.RetrievalRank,
                ContextRank: index + 1));
        }

        return ranked.AsReadOnly();
    }

    private sealed record RelevantMessageSearchResult(
        IReadOnlyList<Message> Messages,
        IReadOnlyList<(Message Message, double Score)> ScoredMessages)
    {
        public static RelevantMessageSearchResult Empty { get; } = new(
            Array.Empty<Message>(),
            Array.Empty<(Message, double)>());
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

    /// <summary>
    /// Resolves the character budget. ~4 chars/token, computed in <c>long</c> and clamped to
    /// <see cref="int.MaxValue"/> so a very large <c>MaxTokens</c> (an "effectively unlimited" value
    /// &gt; ~536M) cannot overflow int and wrap NEGATIVE — which would make <c>totalChars &lt;= maxChars</c>
    /// always false and silently truncate the whole context to empty.
    /// </summary>
    internal static int ResolveMaxChars(ContextBudget budget)
    {
        if (budget.MaxCharacters.HasValue) return budget.MaxCharacters.Value;
        if (budget.MaxTokens.HasValue) return (int)Math.Min((long)budget.MaxTokens.Value * 4, int.MaxValue);
        return int.MaxValue;
    }

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
        int maxChars = ResolveMaxChars(budget);

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
