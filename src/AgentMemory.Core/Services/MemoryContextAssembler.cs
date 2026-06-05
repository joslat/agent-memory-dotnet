using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;

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
    private readonly ILogger<MemoryContextAssembler> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="MemoryContextAssembler"/> class.
    /// </summary>
    public MemoryContextAssembler(
        IShortTermMemoryService shortTerm,
        ILongTermMemoryService longTerm,
        IReasoningMemoryService reasoning,
        IGraphRagContextSource? graphRag,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IClock clock,
        IOptions<MemoryOptions> options,
        ILogger<MemoryContextAssembler> logger)
    {
        _shortTerm = shortTerm;
        _longTerm = longTerm;
        _reasoning = reasoning;
        _graphRag = graphRag;
        _embeddingOrchestrator = embeddingOrchestrator;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
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
                ? _reasoning.SearchSimilarTracesAsync(queryEmbedding, null, recallOpts.MaxTraces, minScore, cancellationToken)
                : Empty<ReasoningTrace>();

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

        int estimatedChars = EstimateChars(recentMessages)
            + EstimateChars(relevantMessages)
            + EstimateChars(entities)
            + EstimateChars(preferences)
            + EstimateChars(facts)
            + EstimateChars(traces)
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
            BlendMode = blendMode
        };

        _logger.LogDebug(
            "Assembled context for session {SessionId}: {Chars} chars (~{Tokens} tokens), truncated={Truncated}",
            request.SessionId, estimatedChars, estimatedChars / 4, truncated);

        return context;
    }

    /// <inheritdoc/>
    public async Task<MemoryContext> AssembleContextAsOfAsync(
        RecallRequest request,
        DateTimeOffset asOf,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Assembling temporal memory context for session {SessionId} as of {AsOf}", request.SessionId, asOf);

        var recallOpts = request.Options;
        var minScore = recallOpts.MinSimilarityScore;

        var queryEmbedding = request.QueryEmbedding
            ?? await _embeddingOrchestrator.EmbedQueryAsync(request.Query, cancellationToken);

        var recentTask = _shortTerm.GetRecentMessagesAsOfAsync(
            request.SessionId, asOf, recallOpts.MaxRecentMessages, cancellationToken);

        var entitiesTask = _longTerm.SearchEntitiesAsOfAsync(
            queryEmbedding, asOf, recallOpts.MaxEntities, minScore, cancellationToken);

        var preferencesTask = _longTerm.SearchPreferencesAsOfAsync(
            queryEmbedding, asOf, recallOpts.MaxPreferences, minScore, cancellationToken);

        var factsTask = _longTerm.SearchFactsAsOfAsync(
            queryEmbedding, asOf, recallOpts.MaxFacts, minScore, cancellationToken);

        await Task.WhenAll(recentTask, entitiesTask, preferencesTask, factsTask);

        var recentMessages = await recentTask;
        var entities = await entitiesTask;
        var preferences = await preferencesTask;
        var facts = await factsTask;

        // Enforce the same context budget as the live recall path so temporal recall cannot blow
        // past the configured token/char limit. (Relevant messages and traces are not part of the
        // temporal snapshot, so they pass through as empty.)
        var budget = _options.ContextBudget;
        if (budget.MaxTokens.HasValue || budget.MaxCharacters.HasValue)
        {
            var fitted = ApplyBudget(
                budget, recentMessages, Array.Empty<Message>(), entities, preferences, facts,
                Array.Empty<ReasoningTrace>(), graphRagContext: null);
            recentMessages = fitted.Recent;
            entities = fitted.Entities;
            preferences = fitted.Preferences;
            facts = fitted.Facts;
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
            SimilarTraces = MemoryContextSection<ReasoningTrace>.Empty,
            Metadata = new Dictionary<string, object> { ["asOf"] = asOf }
        };

        _logger.LogDebug(
            "Assembled temporal context for session {SessionId} as of {AsOf}: {Entities} entities, {Facts} facts, {Prefs} preferences",
            request.SessionId, asOf, entities.Count, facts.Count, preferences.Count);

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

        int totalChars = EstimateChars(recent) + EstimateChars(relevant)
            + EstimateChars(entities) + EstimateChars(preferences)
            + EstimateChars(facts) + EstimateChars(traces)
            + (graphRagContext?.Length ?? 0);

        if (totalChars <= maxChars)
            return new AssembledSections(recent, relevant, entities, preferences, facts, traces, graphRagContext, false);

        return budget.TruncationStrategy switch
        {
            TruncationStrategy.Fail =>
                throw MemoryError.Create($"Context budget exceeded: {totalChars} chars (limit {maxChars}).")
                    .WithCode(MemoryErrorCodes.ContextBudgetExceeded)
                    .WithMetadata("totalChars", totalChars)
                    .WithMetadata("maxChars", maxChars)
                    .Build(),

            TruncationStrategy.OldestFirst =>
                FitByVictim(maxChars, BudgetVictimStrategy.OldestFirst, recent, relevant, entities, preferences, facts, traces, graphRagContext),

            TruncationStrategy.LowestScoreFirst =>
                FitByVictim(maxChars, BudgetVictimStrategy.LowestScoreFirst, recent, relevant, entities, preferences, facts, traces, graphRagContext),

            TruncationStrategy.Proportional =>
                TruncateProportional(maxChars, totalChars, recent, relevant, entities, preferences, facts, traces, graphRagContext),

            _ => FitByVictim(maxChars, BudgetVictimStrategy.OldestFirst, recent, relevant, entities, preferences, facts, traces, graphRagContext)
        };
    }

    private enum BudgetVictimStrategy
    {
        /// <summary>Remove the globally oldest item (by timestamp) across all sections first.</summary>
        OldestFirst,

        /// <summary>Remove the globally lowest-relevance item first, using each item's rank within its
        /// repo-sorted (best-score-first) section as the score proxy — repositories return scored results
        /// sorted by descending similarity, so trailing positions correspond to the lowest scores.</summary>
        LowestScoreFirst
    }

    /// <summary>A removal candidate: identifies one item in one section plus the keys used to rank it.</summary>
    private readonly record struct Victim(int Section, int Index, int Chars, DateTimeOffset Timestamp, double ScoreRank);

    /// <summary>
    /// Fit the assembled sections within <paramref name="maxChars"/> by repeatedly removing the single
    /// "worst" item according to <paramref name="strategy"/> until within budget. GraphRAG (a single
    /// opaque string) is dropped only once every section item has been removed.
    /// </summary>
    private static AssembledSections FitByVictim(
        int maxChars,
        BudgetVictimStrategy strategy,
        IReadOnlyList<Message> recent,
        IReadOnlyList<Message> relevant,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<Preference> preferences,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<ReasoningTrace> traces,
        string? graphRag)
    {
        var recentList = recent.ToList();
        var relevantList = relevant.ToList();
        var entityList = entities.ToList();
        var prefList = preferences.ToList();
        var factList = facts.ToList();
        var traceList = traces.ToList();

        int totalChars = EstimateChars(recentList) + EstimateChars(relevantList)
            + EstimateChars(entityList) + EstimateChars(prefList)
            + EstimateChars(factList) + EstimateChars(traceList)
            + (graphRag?.Length ?? 0);

        while (totalChars > maxChars)
        {
            var victim = SelectVictim(strategy, recentList, relevantList, entityList, prefList, factList, traceList);
            if (victim is null)
            {
                // No section items remain — drop the GraphRAG block last.
                if (graphRag != null) { totalChars -= graphRag.Length; graphRag = null; continue; }
                break;
            }

            totalChars -= victim.Value.Chars;
            RemoveVictim(victim.Value, recentList, relevantList, entityList, prefList, factList, traceList);
        }

        return new AssembledSections(recentList, relevantList, entityList, prefList, factList, traceList, graphRag, true);
    }

    private static Victim? SelectVictim(
        BudgetVictimStrategy strategy,
        List<Message> recent,
        List<Message> relevant,
        List<Entity> entities,
        List<Preference> preferences,
        List<Fact> facts,
        List<ReasoningTrace> traces)
    {
        Victim? worst = null;

        void Consider(int section, int index, int count, int chars, DateTimeOffset timestamp)
        {
            // Sections arrive best-score-first; map position to a [0,1] relevance proxy
            // (1.0 = best in section, 0.0 = worst). A singleton is treated as best-in-section.
            double scoreRank = count <= 1 ? 1.0 : (double)(count - 1 - index) / (count - 1);
            var candidate = new Victim(section, index, chars, timestamp, scoreRank);
            if (worst is null || IsWorse(strategy, candidate, worst.Value))
                worst = candidate;
        }

        for (int i = 0; i < recent.Count; i++) Consider(0, i, recent.Count, EstimateItemChars(recent[i]), recent[i].TimestampUtc);
        for (int i = 0; i < relevant.Count; i++) Consider(1, i, relevant.Count, EstimateItemChars(relevant[i]), relevant[i].TimestampUtc);
        for (int i = 0; i < entities.Count; i++) Consider(2, i, entities.Count, EstimateItemChars(entities[i]), entities[i].CreatedAtUtc);
        for (int i = 0; i < preferences.Count; i++) Consider(3, i, preferences.Count, EstimateItemChars(preferences[i]), preferences[i].CreatedAtUtc);
        for (int i = 0; i < facts.Count; i++) Consider(4, i, facts.Count, EstimateItemChars(facts[i]), facts[i].CreatedAtUtc);
        for (int i = 0; i < traces.Count; i++) Consider(5, i, traces.Count, EstimateItemChars(traces[i]), traces[i].StartedAtUtc);

        return worst;
    }

    private static bool IsWorse(BudgetVictimStrategy strategy, Victim candidate, Victim current) => strategy switch
    {
        // Oldest timestamp loses first; ties broken by lowest relevance.
        BudgetVictimStrategy.OldestFirst =>
            candidate.Timestamp < current.Timestamp
            || (candidate.Timestamp == current.Timestamp && candidate.ScoreRank < current.ScoreRank),

        // Lowest relevance loses first; ties broken by oldest timestamp.
        BudgetVictimStrategy.LowestScoreFirst =>
            candidate.ScoreRank < current.ScoreRank
            || (candidate.ScoreRank == current.ScoreRank && candidate.Timestamp < current.Timestamp),

        _ => false
    };

    private static void RemoveVictim(
        Victim victim,
        List<Message> recent,
        List<Message> relevant,
        List<Entity> entities,
        List<Preference> preferences,
        List<Fact> facts,
        List<ReasoningTrace> traces)
    {
        switch (victim.Section)
        {
            case 0: recent.RemoveAt(victim.Index); break;
            case 1: relevant.RemoveAt(victim.Index); break;
            case 2: entities.RemoveAt(victim.Index); break;
            case 3: preferences.RemoveAt(victim.Index); break;
            case 4: facts.RemoveAt(victim.Index); break;
            case 5: traces.RemoveAt(victim.Index); break;
        }
    }

    private static AssembledSections TruncateProportional(
        int maxChars,
        int totalChars,
        IReadOnlyList<Message> recent,
        IReadOnlyList<Message> relevant,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<Preference> preferences,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<ReasoningTrace> traces,
        string? graphRag)
    {
        double ratio = (double)maxChars / totalChars;

        var trimmedRecent = TrimToRatio(recent, EstimateChars(recent), ratio);
        var trimmedRelevant = TrimToRatio(relevant, EstimateChars(relevant), ratio);
        var trimmedEntities = TrimToRatio(entities, EstimateChars(entities), ratio);
        var trimmedPreferences = TrimToRatio(preferences, EstimateChars(preferences), ratio);
        var trimmedFacts = TrimToRatio(facts, EstimateChars(facts), ratio);
        var trimmedTraces = TrimToRatio(traces, EstimateChars(traces), ratio);

        string? trimmedGraphRag = graphRag;
        if (graphRag != null)
        {
            int graphRagBudget = (int)(graphRag.Length * ratio);
            trimmedGraphRag = graphRag.Length > graphRagBudget
                ? graphRag[..graphRagBudget]
                : graphRag;
        }

        return new AssembledSections(trimmedRecent, trimmedRelevant, trimmedEntities,
            trimmedPreferences, trimmedFacts, trimmedTraces, trimmedGraphRag, true);
    }

    private static IReadOnlyList<T> TrimToRatio<T>(IReadOnlyList<T> items, int currentChars, double ratio)
    {
        if (items.Count == 0) return items;
        int targetChars = (int)(currentChars * ratio);
        int runningChars = 0;
        int keepCount = 0;
        foreach (var item in items)
        {
            int itemChars = EstimateItemChars(item);
            if (runningChars + itemChars > targetChars) break;
            runningChars += itemChars;
            keepCount++;
        }
        return items.Take(keepCount).ToList();
    }

    private static int EstimateChars<T>(IReadOnlyList<T> items) =>
        items.Sum(EstimateItemChars);

    private static int EstimateItemChars<T>(T item) => item switch
    {
        // Every item costs at least 1 char so that budget truncation always makes forward progress —
        // an empty-content message/preference must still reduce the running total when removed,
        // otherwise the victim loop could churn through zero-cost items without nearing the budget.
        Message m => Math.Max(1, m.Content.Length),
        Entity e => (e.Name?.Length ?? 0) + (e.Description?.Length ?? 0) + 10,
        Fact f => f.Subject.Length + f.Predicate.Length + f.Object.Length + 4,
        Preference p => Math.Max(1, p.PreferenceText.Length),
        ReasoningTrace t => t.Task.Length + (t.Outcome?.Length ?? 0) + 10,
        _ => 50
    };
}
