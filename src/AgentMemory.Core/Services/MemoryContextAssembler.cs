using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
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

    /// <summary>
    /// Builds a section's diagnostics from what the retrieval actually did.
    /// </summary>
    /// <remarks>
    /// <paramref name="searched"/> is passed in rather than inferred from the item count, because the
    /// whole point is to separate "searched and found nothing" from "never searched" — and those look
    /// identical from the results alone.
    /// </remarks>
    private static MemoryContextSectionDiagnostics Diagnose<T>(
        bool searched,
        int requestedLimit,
        IReadOnlyList<T> items,
        IReadOnlyList<MemoryContextRankedItem> ranked,
        double minimumScore,
        bool timedOut = false)
    {
        // Scores come from the ranked items, which exist only when the provider could supply them.
        // An unscoreable section reports null rather than a placeholder: zero is a real score.
        double? top = null, low = null;
        if (ranked.Count > 0)
        {
            top = ranked.Max(item => item.Score);
            low = ranked.Min(item => item.Score);
        }

        return new MemoryContextSectionDiagnostics(
            searched, requestedLimit, items.Count, top, low, minimumScore, timedOut);
    }

    /// <summary>
    /// Whether any retrieval selected by <paramref name="recallOpts"/> actually consumes a query vector.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exactly the five categories whose searches are gated on <c>hasEmbedding</c>. Recent messages are
    /// session-scoped and time-ordered, so they need no vector; GraphRAG builds its own retriever and
    /// starts before this point, so it needs nothing from here either.
    /// </para>
    /// <para>
    /// <b>One definition, deliberately.</b> The live and as-of paths repeat their per-category gates
    /// independently and have already drifted apart once on a single option, so the decision that spans
    /// both lives in one place where a new category cannot be added to one and forgotten in the other.
    /// </para>
    /// </remarks>
    private static bool NeedsQueryEmbedding(RecallOptions recallOpts) =>
        recallOpts.MaxRelevantMessages > 0
        || recallOpts.MaxEntities > 0
        || recallOpts.MaxPreferences > 0
        || recallOpts.MaxFacts > 0
        || recallOpts.MaxTraces > 0;

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
        // Retrieval scores per section, populated only on the diagnostics path below. Empty — never a
        // placeholder score — for any section whose provider could not supply one, so a reader can tell
        // "retrieved weakly" apart from "not ranked at all".
        IReadOnlyList<(Entity Entity, double Score)> entityScores = Array.Empty<(Entity, double)>();
        IReadOnlyList<(Preference Preference, double Score)> preferenceScores = Array.Empty<(Preference, double)>();
        IReadOnlyList<(Fact Fact, double Score)> factScores = Array.Empty<(Fact, double)>();
        IReadOnlyList<(ReasoningTrace Trace, double Score)> traceScores = Array.Empty<(ReasoningTrace, double)>();

        // Rank 13 bookkeeping, at METHOD scope: the fan-out lives in a nested block and these are read
        // by the diagnostics built after it closes. A section that searched and found nothing and one
        // that never came back produce an identical section; only one means the answer is incomplete.
        var budgetExceeded = false;
        bool droppedRecent = false, droppedRelevant = false, droppedEntities = false,
             droppedPreferences = false, droppedFacts = false, droppedTraces = false;

        // J2.2 resolution, computed once at method scope so it can be both used by the fact search
        // and reported on the context. Which relations a question resolved to is the difference
        // between "expansion had nothing to expand" and "expansion ran and did not help", and those
        // need opposite responses. It was computed inline and discarded, so no report could tell
        // them apart.
        var resolvedQueryRelations = recallOpts.ExpandFactsByPredicate &&
                                     recallOpts.ResolveQueryRelations
            ? MemoryRelationLexicon.Default.ResolveQuestion(request.Query)
            : Array.Empty<string>();

        // Whether the vector searches were reachable at all this recall, hoisted so the diagnostics built
        // after budgeting can distinguish "searched and found nothing" from "never searched". False when
        // memory was excluded by blend mode, or when no embedding was available to search with.
        bool vectorSearchRan = false;

        if (includeMemory)
        {
            // Generate embedding if not provided, and ONLY if some retrieval will actually consume it.
            // Every vector search below is gated on its own MaxX > 0 (#88); the embedding was not, so a
            // task-aware policy that narrowed a turn to recent messages alone still paid for a provider
            // round trip whose result nothing read. Remote-shaped that is the single largest recall
            // stage (~120 ms), which made category narrowing *relocate* the cost rather than remove it.
            // Gating it here rather than in the MAF provider also covers the SK adapter, the CLI and the
            // facade, which all reach this method.
            bool needsEmbedding = NeedsQueryEmbedding(recallOpts);

            var queryEmbedding = request.QueryEmbedding
                ?? (needsEmbedding
                    ? await TimedAsync(
                            "memory.recall.embedding",
                            () => _embeddingOrchestrator.EmbedQueryAsync(request.Query, cancellationToken))
                        .ConfigureAwait(false)
                    : Array.Empty<float>());

            // Vector searches require a non-empty embedding. A blank query (e.g. a history-only
            // recall via the chat-history provider) yields an empty embedding — skip the semantic
            // searches rather than issue zero-dimension vector queries (which the index rejects).
            bool hasEmbedding = queryEmbedding is { Length: > 0 };
            vectorSearchRan = hasEmbedding;
            static Task<IReadOnlyList<T>> Empty<T>() => Task.FromResult<IReadOnlyList<T>>(Array.Empty<T>());

            // Every long-term/reasoning search below is ranked by the vector index and the service layer
            // then throws the score away, which is why four of the five sections have always shipped an
            // empty RankedItems. The internal scored contracts hand the same result back WITH its scores
            // and WITHOUT a second query. Null when diagnostics are off (the default) or when a custom
            // service implementation does not expose the contract, and every call below is then the
            // pre-existing one — same query, same allocations, same behaviour.
            var scoredLongTerm = recallOpts.IncludeDiagnostics ? _longTerm as IScoredLongTermSearch : null;
            var scoredReasoning = recallOpts.IncludeDiagnostics ? _reasoning as IScoredTraceSearch : null;

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

            // Each section starts exactly one retrieval: the scored variant when this recall asked for
            // diagnostics and the provider can serve them, the pre-existing unscored one otherwise. The
            // unused handle is a completed Empty task, so nothing is ever retrieved twice.
            bool searchEntities = hasEmbedding && recallOpts.MaxEntities > 0;
            bool searchPreferences = hasEmbedding && recallOpts.MaxPreferences > 0;
            bool searchFacts = hasEmbedding && recallOpts.MaxFacts > 0;
            bool searchTraces = hasEmbedding && recallOpts.MaxTraces > 0;

            var entitiesTask = searchEntities && scoredLongTerm is null
                ? TimedAsync("memory.recall.entities",
                    () => _longTerm.SearchEntitiesAsync(queryEmbedding, recallOpts.MaxEntities, minScore, scope, cancellationToken))
                : Empty<Entity>();
            var entitiesScoredTask = searchEntities && scoredLongTerm is not null
                ? TimedAsync("memory.recall.entities",
                    () => scoredLongTerm!.SearchEntitiesWithScoresAsync(queryEmbedding, recallOpts.MaxEntities, minScore, scope, cancellationToken))
                : null;

            var preferencesTask = searchPreferences && scoredLongTerm is null
                ? TimedAsync("memory.recall.preferences",
                    () => _longTerm.SearchPreferencesAsync(queryEmbedding, recallOpts.MaxPreferences, minScore, scope, cancellationToken))
                : Empty<Preference>();
            var preferencesScoredTask = searchPreferences && scoredLongTerm is not null
                ? TimedAsync("memory.recall.preferences",
                    () => scoredLongTerm!.SearchPreferencesWithScoresAsync(queryEmbedding, recallOpts.MaxPreferences, minScore, scope, cancellationToken))
                : null;

            // The scored fact search takes the widest overload unconditionally because it can: the
            // expansion arguments below are the same values the unscored calls pass, and false/0/empty
            // reproduces the narrow overload exactly.
            var factsScoredTask = searchFacts && scoredLongTerm is not null
                ? TimedAsync("memory.recall.facts",
                    () => scoredLongTerm!.SearchFactsWithScoresAsync(
                        queryEmbedding, recallOpts.MaxFacts, minScore, scope,
                        recallOpts.ExpandFactsByPredicate,
                        recallOpts.ExpandFactsByPredicate ? recallOpts.MaxExpandedFacts : 0,
                        resolvedQueryRelations,
                        cancellationToken))
                : null;

            var factsTask = searchFacts && scoredLongTerm is null
                ? TimedAsync("memory.recall.facts",
                    // Only the expansion path takes the wider overload. Off by default, the call is
                    // byte-for-byte the original, so no existing behaviour or contract shifts.
                    // Hoisted out of the call so the decision is observable. Which relations a
                    // question resolved to is the difference between "expansion had nothing to
                    // expand" and "expansion ran and did not help", and the two need opposite
                    // responses. It was computed inline and discarded, so no report could tell them
                    // apart - a multi-session question failing because its verb is absent from the
                    // table looked identical to one failing for any other reason.
                    () => recallOpts.ExpandFactsByPredicate
                        ? _longTerm.SearchFactsAsync(
                            queryEmbedding, recallOpts.MaxFacts, minScore, scope,
                            true, recallOpts.MaxExpandedFacts,
                            // J2.2. Empty unless explicitly enabled, and an unrecognised verb resolves
                            // to nothing, so both the option-off and the no-match paths reproduce the
                            // previous call exactly.
                            resolvedQueryRelations,
                            cancellationToken)
                        // Valid time is applied on the non-expansion path only. Expansion fetches by
                        // predicate rather than by vector and would need its own clause; gating one and
                        // silently not the other would be worse than gating neither, so expansion keeps
                        // today's behaviour until it is done deliberately.
                        // Same rule as the service below it: take the pre-existing overload unless the
                        // gate is on, so an ungated recall is byte-identical to what it always was.
                        : recallOpts.ValidTime == ValidTimeMode.Ignore
                            ? _longTerm.SearchFactsAsync(
                                queryEmbedding, recallOpts.MaxFacts, minScore, scope, cancellationToken)
                            : _longTerm.SearchFactsAsync(
                                queryEmbedding, recallOpts.ValidTime, recallOpts.MaxFacts, minScore,
                                scope, cancellationToken))
                : Empty<Fact>();

            var tracesTask = searchTraces && scoredReasoning is null
                ? TimedAsync("memory.recall.traces",
                    () => _reasoning.SearchSimilarTracesAsync(
                        queryEmbedding,
                        // K5: was a hardcoded null, so the outcome filter the repository and its
                        // Cypher already supported could never be reached from automatic recall.
                        // A recalled trace is shown to the reader as precedent with nothing marking
                        // it as a failure, so imitating reasoning that did not work is worse than
                        // recalling nothing. Default stays null - today's behaviour - until measured.
                        recallOpts.SuccessfulTracesOnly,
                        recallOpts.MaxTraces, minScore, scope, cancellationToken))
                : Empty<ReasoningTrace>();
            var tracesScoredTask = searchTraces && scoredReasoning is not null
                ? TimedAsync("memory.recall.traces",
                    () => scoredReasoning!.SearchSimilarTracesWithScoresAsync(
                        queryEmbedding, recallOpts.SuccessfulTracesOnly,
                        recallOpts.MaxTraces, minScore, scope, cancellationToken))
                : null;

            if (overrideRanking) _rankingContext!.Current = null;

            // One handle per section, so a fault in any of them is still observed here rather than
            // surfacing later as an unobserved task exception.
            Task[] sections =
            [
                recentTask, relevantTask,
                entitiesScoredTask ?? (Task)entitiesTask,
                preferencesScoredTask ?? (Task)preferencesTask,
                factsScoredTask ?? (Task)factsTask,
                tracesScoredTask ?? (Task)tracesTask,
            ];

            // Rank 13. With no budget this is the original unconditional wait, byte for byte.
            if (recallOpts.LatencyBudget is { } latencyBudget
                && await WaitWithinBudgetAsync(sections, latencyBudget, cancellationToken).ConfigureAwait(false))
            {
                // Each unfinished section is replaced by an empty result AND recorded, because the two
                // have to be told apart downstream: "searched and found nothing" and "never came back"
                // produce an identical section, and only one of them means the answer is incomplete.
                budgetExceeded = true;
                if (DropIfUnfinished<IReadOnlyList<Message>>(ref recentTask, [])) droppedRecent = true;
                if (DropIfUnfinished(ref relevantTask, RelevantMessageSearchResult.Empty)) droppedRelevant = true;
                if (entitiesScoredTask is not null)
                    droppedEntities = DropIfUnfinished<IReadOnlyList<(Entity Entity, double Score)>>(ref entitiesScoredTask, []);
                else droppedEntities = DropIfUnfinished<IReadOnlyList<Entity>>(ref entitiesTask, []);
                if (preferencesScoredTask is not null)
                    droppedPreferences = DropIfUnfinished<IReadOnlyList<(Preference Preference, double Score)>>(ref preferencesScoredTask, []);
                else droppedPreferences = DropIfUnfinished<IReadOnlyList<Preference>>(ref preferencesTask, []);
                if (factsScoredTask is not null)
                    droppedFacts = DropIfUnfinished(ref factsScoredTask, new ScoredFactSearchResult([], []));
                else droppedFacts = DropIfUnfinished<IReadOnlyList<Fact>>(ref factsTask, []);
                if (tracesScoredTask is not null)
                    droppedTraces = DropIfUnfinished<IReadOnlyList<(ReasoningTrace Trace, double Score)>>(ref tracesScoredTask, []);
                else droppedTraces = DropIfUnfinished<IReadOnlyList<ReasoningTrace>>(ref tracesTask, []);

                _logger.LogWarning(
                    "Recall latency budget of {Budget}ms expired; returning partial context.",
                    latencyBudget.TotalMilliseconds);
            }

            recentMessages = await recentTask.ConfigureAwait(false);
            var relevantResult = await relevantTask.ConfigureAwait(false);
            relevantMessages = relevantResult.Messages;
            relevantMessageScores = relevantResult.ScoredMessages;

            if (entitiesScoredTask is null)
            {
                entities = await entitiesTask.ConfigureAwait(false);
            }
            else
            {
                entityScores = await entitiesScoredTask.ConfigureAwait(false);
                entities = entityScores.Select(static scored => scored.Entity).ToArray();
            }

            if (preferencesScoredTask is null)
            {
                preferences = await preferencesTask.ConfigureAwait(false);
            }
            else
            {
                preferenceScores = await preferencesScoredTask.ConfigureAwait(false);
                preferences = preferenceScores.Select(static scored => scored.Preference).ToArray();
            }

            if (factsScoredTask is null)
            {
                facts = await factsTask.ConfigureAwait(false);
            }
            else
            {
                // Facts are the one section where Items ⊋ the scored set: predicate expansion appends
                // facts retrieved by predicate rather than by similarity, so they arrive with no score
                // and are simply absent from factScores.
                var factResult = await factsScoredTask.ConfigureAwait(false);
                facts = factResult.Facts;
                factScores = factResult.Scored;
            }

            if (tracesScoredTask is null)
            {
                traces = await tracesTask.ConfigureAwait(false);
            }
            else
            {
                traceScores = await tracesScoredTask.ConfigureAwait(false);
                traces = traceScores.Select(static scored => scored.Trace).ToArray();
            }
        }

        if (graphRagTask != null)
            await graphRagTask.ConfigureAwait(false);

        string? graphRagContext = null;
        // K4: the items are retained, not only their joined text. They already carry SourceNodeIds,
        // Score and Metadata, and discarding them here is what left GraphRAG unattributable, and so
        // unmeasurable, in every quality run to date. The joined string is byte-identical to before.
        IReadOnlyList<GraphRagContextItem> graphRagItems = Array.Empty<GraphRagContextItem>();
        if (graphRagTask != null)
        {
            var graphRagResult = await graphRagTask.ConfigureAwait(false);
            if (graphRagResult?.Items is { Count: > 0 } items)
            {
                graphRagContext = string.Join("\n\n", items.Select(i => i.Text));
                graphRagItems = items;
            }
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

        // Built after budgeting, because ContextRank means "position among the items that SURVIVED the
        // budget" while RetrievalRank keeps the provider's original ordering. Off by default: every
        // section then keeps its Array.Empty default and none of this runs.
        IReadOnlyList<MemoryContextRankedItem> relevantRanked = Array.Empty<MemoryContextRankedItem>();
        IReadOnlyList<MemoryContextRankedItem> entityRanked = Array.Empty<MemoryContextRankedItem>();
        IReadOnlyList<MemoryContextRankedItem> preferenceRanked = Array.Empty<MemoryContextRankedItem>();
        IReadOnlyList<MemoryContextRankedItem> factRanked = Array.Empty<MemoryContextRankedItem>();
        IReadOnlyList<MemoryContextRankedItem> traceRanked = Array.Empty<MemoryContextRankedItem>();

        if (recallOpts.IncludeDiagnostics)
        {
            // Any section whose scores stayed empty (provider without the scored contract, or a section
            // that was never searched) falls straight back out of BuildRankedItems as empty — an
            // unscoreable section reports nothing rather than a made-up score.
            relevantRanked = BuildRankedItems(relevantMessages, relevantMessageScores, static m => m.MessageId);
            entityRanked = BuildRankedItems(entities, entityScores, static e => e.EntityId);
            preferenceRanked = BuildRankedItems(preferences, preferenceScores, static p => p.PreferenceId);
            factRanked = BuildRankedItems(facts, factScores, static f => f.FactId);
            traceRanked = BuildRankedItems(traces, traceScores, static t => t.TraceId);
        }

        var context = new MemoryContext
        {
            SessionId = request.SessionId,
            AssembledAtUtc = _clock.UtcNow,
            RecentMessages = new MemoryContextSection<Message>
            {
                Items = recentMessages,
                // No vector involved: session-scoped and time-ordered, so the limit alone decides.
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(recallOpts.MaxRecentMessages > 0, recallOpts.MaxRecentMessages,
                        recentMessages, Array.Empty<MemoryContextRankedItem>(), minScore, droppedRecent)
                    : null
            },
            RelevantMessages = new MemoryContextSection<Message>
            {
                Items = relevantMessages,
                RankedItems = relevantRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(vectorSearchRan && recallOpts.MaxRelevantMessages > 0,
                        recallOpts.MaxRelevantMessages, relevantMessages, relevantRanked, minScore, droppedRelevant)
                    : null
            },
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = entities,
                RankedItems = entityRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(vectorSearchRan && recallOpts.MaxEntities > 0,
                        recallOpts.MaxEntities, entities, entityRanked, minScore, droppedEntities)
                    : null
            },
            RelevantPreferences = new MemoryContextSection<Preference>
            {
                Items = preferences,
                RankedItems = preferenceRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(vectorSearchRan && recallOpts.MaxPreferences > 0,
                        recallOpts.MaxPreferences, preferences, preferenceRanked, minScore, droppedPreferences)
                    : null
            },
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = facts,
                RankedItems = factRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(vectorSearchRan && recallOpts.MaxFacts > 0,
                        recallOpts.MaxFacts, facts, factRanked, minScore, droppedFacts)
                    : null
            },
            SimilarTraces = new MemoryContextSection<ReasoningTrace>
            {
                Items = traces,
                RankedItems = traceRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(vectorSearchRan && recallOpts.MaxTraces > 0,
                        recallOpts.MaxTraces, traces, traceRanked, minScore, droppedTraces)
                    : null
            },
            GraphRagContext = graphRagContext,
            GraphRagItems = graphRagItems,
            ResolvedQueryRelations = resolvedQueryRelations,
            BlendMode = blendMode,
            Truncated = truncated,
            LatencyBudgetExceeded = budgetExceeded
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

        // Same elision as the live path, asserted separately rather than assumed: these two paths have
        // already diverged once on a single option (SuccessfulTracesOnly is passed live and hardcoded
        // null here), so "it mirrors the live path" is not a safe thing to take on trust.
        var queryEmbedding = request.QueryEmbedding
            ?? (NeedsQueryEmbedding(recallOpts)
                ? await _embeddingOrchestrator.EmbedQueryAsync(request.Query, cancellationToken).ConfigureAwait(false)
                : Array.Empty<float>());

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

        // Same diagnostics gate as the live path, because this assembles the same five sections from a
        // second code path — an instrument wired into only one of the two recall paths would report a
        // point-in-time recall as unscored when it merely went the other way. Null when diagnostics are
        // off (the default), and every call below is then the pre-existing one.
        var scoredLongTerm = recallOpts.IncludeDiagnostics ? _longTerm as IScoredLongTermSearch : null;
        var scoredReasoning = recallOpts.IncludeDiagnostics ? _reasoning as IScoredTraceSearch : null;
        bool searchEntities = hasEmbedding && recallOpts.MaxEntities > 0;
        bool searchPreferences = hasEmbedding && recallOpts.MaxPreferences > 0;
        bool searchFacts = hasEmbedding && recallOpts.MaxFacts > 0;
        bool searchTraces = hasEmbedding && recallOpts.MaxTraces > 0;

        var entitiesTask = searchEntities && scoredLongTerm is null
            ? _longTerm.SearchEntitiesAsOfAsync(queryEmbedding, systemAsOf, recallOpts.MaxEntities, minScore, scope, cancellationToken)
            : Empty<Entity>();
        var entitiesScoredTask = searchEntities && scoredLongTerm is not null
            ? scoredLongTerm!.SearchEntitiesAsOfWithScoresAsync(queryEmbedding, systemAsOf, recallOpts.MaxEntities, minScore, scope, cancellationToken)
            : null;

        var preferencesTask = searchPreferences && scoredLongTerm is null
            ? _longTerm.SearchPreferencesAsOfAsync(queryEmbedding, systemAsOf, recallOpts.MaxPreferences, minScore, scope, cancellationToken)
            : Empty<Preference>();
        var preferencesScoredTask = searchPreferences && scoredLongTerm is not null
            ? scoredLongTerm!.SearchPreferencesAsOfWithScoresAsync(queryEmbedding, systemAsOf, recallOpts.MaxPreferences, minScore, scope, cancellationToken)
            : null;

        var factsTask = searchFacts && scoredLongTerm is null
            ? _longTerm.SearchFactsAsOfAsync(queryEmbedding, validAsOf, recallOpts.MaxFacts, minScore, scope, systemAsOf, cancellationToken)
            : Empty<Fact>();
        var factsScoredTask = searchFacts && scoredLongTerm is not null
            ? scoredLongTerm!.SearchFactsAsOfWithScoresAsync(queryEmbedding, validAsOf, recallOpts.MaxFacts, minScore, scope, systemAsOf, cancellationToken)
            : null;

        var tracesTask = searchTraces && scoredReasoning is null
            // Same option, same meaning, both recall paths. This was a hardcoded null while the live
            // path (line ~267) forwarded the caller's choice, so asking for successful-only was honoured
            // by AssembleContextAsync and silently ignored here. Point-in-time semantics do not justify
            // dropping it: ReasoningQueries.SearchByTaskVectorAsOf applies the same
            // `node.success = $successFilter` predicate as the live query, adds only
            // `node.started_at <= datetime($asOf)`, and returns the trace's present-time success on the
            // row either way — so filtering on it reveals nothing the result did not already carry.
            // Default is null, so unset behaviour here is byte-for-byte what it was.
            ? _reasoning.SearchSimilarTracesAsOfAsync(queryEmbedding, systemAsOf, recallOpts.SuccessfulTracesOnly, recallOpts.MaxTraces, minScore, scope, cancellationToken)
            : Empty<ReasoningTrace>();
        var tracesScoredTask = searchTraces && scoredReasoning is not null
            ? scoredReasoning!.SearchSimilarTracesAsOfWithScoresAsync(
                queryEmbedding, systemAsOf, recallOpts.SuccessfulTracesOnly, recallOpts.MaxTraces, minScore, scope, cancellationToken)
            : null;

        await Task.WhenAll(
            recentTask,
            entitiesScoredTask ?? (Task)entitiesTask,
            preferencesScoredTask ?? (Task)preferencesTask,
            factsScoredTask ?? (Task)factsTask,
            tracesScoredTask ?? (Task)tracesTask).ConfigureAwait(false);

        var recentMessages = await recentTask.ConfigureAwait(false);

        // Scores for the sections that were searched with the scored contract; empty — never fabricated —
        // for any section that was not.
        IReadOnlyList<(Entity Entity, double Score)> entityScores = Array.Empty<(Entity, double)>();
        IReadOnlyList<(Preference Preference, double Score)> preferenceScores = Array.Empty<(Preference, double)>();
        IReadOnlyList<(Fact Fact, double Score)> factScores = Array.Empty<(Fact, double)>();
        IReadOnlyList<(ReasoningTrace Trace, double Score)> traceScores = Array.Empty<(ReasoningTrace, double)>();

        IReadOnlyList<Entity> entities;
        if (entitiesScoredTask is null)
        {
            entities = await entitiesTask.ConfigureAwait(false);
        }
        else
        {
            entityScores = await entitiesScoredTask.ConfigureAwait(false);
            entities = entityScores.Select(static scored => scored.Entity).ToArray();
        }

        IReadOnlyList<Preference> preferences;
        if (preferencesScoredTask is null)
        {
            preferences = await preferencesTask.ConfigureAwait(false);
        }
        else
        {
            preferenceScores = await preferencesScoredTask.ConfigureAwait(false);
            preferences = preferenceScores.Select(static scored => scored.Preference).ToArray();
        }

        IReadOnlyList<Fact> facts;
        if (factsScoredTask is null)
        {
            facts = await factsTask.ConfigureAwait(false);
        }
        else
        {
            // No predicate expansion on the point-in-time path, so unlike the live recall every fact here
            // is scored and Items and Scored hold the same set.
            factScores = await factsScoredTask.ConfigureAwait(false);
            facts = factScores.Select(static scored => scored.Fact).ToArray();
        }

        IReadOnlyList<ReasoningTrace> traces;
        if (tracesScoredTask is null)
        {
            traces = await tracesTask.ConfigureAwait(false);
        }
        else
        {
            traceScores = await tracesScoredTask.ConfigureAwait(false);
            traces = traceScores.Select(static scored => scored.Trace).ToArray();
        }

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

        // Built after budgeting for the same reason as the live path: ContextRank is the post-budget
        // position. Off by default — every section then keeps its Array.Empty default.
        IReadOnlyList<MemoryContextRankedItem> entityRanked = Array.Empty<MemoryContextRankedItem>();
        IReadOnlyList<MemoryContextRankedItem> preferenceRanked = Array.Empty<MemoryContextRankedItem>();
        IReadOnlyList<MemoryContextRankedItem> factRanked = Array.Empty<MemoryContextRankedItem>();
        IReadOnlyList<MemoryContextRankedItem> traceRanked = Array.Empty<MemoryContextRankedItem>();

        if (recallOpts.IncludeDiagnostics)
        {
            entityRanked = BuildRankedItems(entities, entityScores, static e => e.EntityId);
            preferenceRanked = BuildRankedItems(preferences, preferenceScores, static p => p.PreferenceId);
            factRanked = BuildRankedItems(facts, factScores, static f => f.FactId);
            traceRanked = BuildRankedItems(traces, traceScores, static t => t.TraceId);
        }

        var context = new MemoryContext
        {
            SessionId = request.SessionId,
            AssembledAtUtc = _clock.UtcNow,
            RecentMessages = new MemoryContextSection<Message>
            {
                Items = recentMessages,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(recallOpts.MaxRecentMessages > 0, recallOpts.MaxRecentMessages,
                        recentMessages, Array.Empty<MemoryContextRankedItem>(), minScore)
                    : null
            },
            // Empty, not unscored: the temporal snapshot runs no semantic message search at all, so there
            // is no retrieval here to report a rank for.
            RelevantMessages = MemoryContextSection<Message>.Empty,
            RelevantEntities = new MemoryContextSection<Entity>
            {
                Items = entities,
                RankedItems = entityRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(hasEmbedding && recallOpts.MaxEntities > 0,
                        recallOpts.MaxEntities, entities, entityRanked, minScore)
                    : null
            },
            RelevantPreferences = new MemoryContextSection<Preference>
            {
                Items = preferences,
                RankedItems = preferenceRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(hasEmbedding && recallOpts.MaxPreferences > 0,
                        recallOpts.MaxPreferences, preferences, preferenceRanked, minScore)
                    : null
            },
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = facts,
                RankedItems = factRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(hasEmbedding && recallOpts.MaxFacts > 0,
                        recallOpts.MaxFacts, facts, factRanked, minScore)
                    : null
            },
            SimilarTraces = new MemoryContextSection<ReasoningTrace>
            {
                Items = traces,
                RankedItems = traceRanked,
                Diagnostics = recallOpts.IncludeDiagnostics
                    ? Diagnose(hasEmbedding && recallOpts.MaxTraces > 0,
                        recallOpts.MaxTraces, traces, traceRanked, minScore)
                    : null
            },
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

    /// <summary>
    /// Joins the items that made it into a context section against the scored results the provider
    /// returned, producing one <see cref="MemoryContextRankedItem"/> per item that has a score.
    /// </summary>
    /// <remarks>
    /// Generic over the item type so all five sections share ONE ranking path — the per-type difference is
    /// only which property is the stable id, supplied by <paramref name="idOf"/>. An item present in
    /// <paramref name="contextItems"/> but absent from <paramref name="retrievedItems"/> is SKIPPED rather
    /// than emitted with a stand-in score: fact predicate-expansion legitimately produces such items, and
    /// a fabricated score there would be indistinguishable from a real one.
    /// </remarks>
    internal static IReadOnlyList<MemoryContextRankedItem> BuildRankedItems<T>(
        IReadOnlyList<T> contextItems,
        IReadOnlyList<(T Item, double Score)> retrievedItems,
        Func<T, string> idOf)
    {
        if (contextItems.Count == 0 || retrievedItems.Count == 0)
            return Array.Empty<MemoryContextRankedItem>();

        var retrievedById = retrievedItems
            .Select((result, index) => new
            {
                ItemId = idOf(result.Item),
                result.Score,
                RetrievalRank = index + 1
            })
            .ToDictionary(result => result.ItemId, StringComparer.Ordinal);

        var ranked = new List<MemoryContextRankedItem>(contextItems.Count);
        for (var index = 0; index < contextItems.Count; index++)
        {
            var itemId = idOf(contextItems[index]);
            if (!retrievedById.TryGetValue(itemId, out var retrieved))
                continue;
            ranked.Add(new MemoryContextRankedItem(
                itemId,
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

    /// <summary>
    /// Waits for the section tasks, giving up after <paramref name="budget"/> (rank 13).
    /// </summary>
    /// <returns><see langword="true"/> when the budget expired with work still running.</returns>
    /// <remarks>
    /// <para>
    /// The still-running queries are <b>not</b> cancelled. They are already in the driver, and tearing
    /// them down buys nothing the caller is waiting for — it only turns a wasted result into a wasted
    /// result plus a cancellation storm. Their faults are observed so a slow section that also fails
    /// does not surface later as an unobserved task exception.
    /// </para>
    /// </remarks>
    private static async Task<bool> WaitWithinBudgetAsync(
        Task[] sections, TimeSpan budget, CancellationToken cancellationToken)
    {
        var all = Task.WhenAll(sections);
        using var expiry = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var timer = Task.Delay(budget, expiry.Token);

        var finished = await Task.WhenAny(all, timer).ConfigureAwait(false);
        if (finished == all)
        {
            await expiry.CancelAsync().ConfigureAwait(false);
            await all.ConfigureAwait(false); // rethrow section faults exactly as before
            return false;
        }

        foreach (var section in sections)
        {
            _ = section.ContinueWith(
                faulted => _ = faulted.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }

        return true;
    }

    /// <summary>
    /// Replaces a section that did not finish in time with an empty result (rank 13).
    /// </summary>
    /// <returns><see langword="true"/> when a substitution happened, i.e. this section was dropped.</returns>
    private static bool DropIfUnfinished<T>(ref Task<T> section, T empty)
    {
        if (section.IsCompletedSuccessfully) return false;
        section = Task.FromResult(empty);
        return true;
    }
}
