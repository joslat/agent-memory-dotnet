using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Exceptions;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

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

    public async Task<MemoryContext> AssembleContextAsync(
        RecallRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Assembling memory context for session {SessionId}", request.SessionId);

        var recallOpts = request.Options;
        var minScore = recallOpts.MinSimilarityScore;

        // Generate embedding if not provided
        var queryEmbedding = request.QueryEmbedding;
        if (queryEmbedding is null)
        {
            queryEmbedding = await _embeddingOrchestrator.EmbedQueryAsync(request.Query, cancellationToken);
        }

        // Launch all retrieval tasks in parallel
        var recentTask = _shortTerm.GetRecentMessagesAsync(
            request.SessionId, recallOpts.MaxRecentMessages, cancellationToken);

        var relevantTask = _shortTerm.SearchMessagesAsync(
            request.SessionId, queryEmbedding, recallOpts.MaxRelevantMessages, minScore, cancellationToken);

        var entitiesTask = _longTerm.SearchEntitiesAsync(
            queryEmbedding, recallOpts.MaxEntities, minScore, cancellationToken);

        var preferencesTask = _longTerm.SearchPreferencesAsync(
            queryEmbedding, recallOpts.MaxPreferences, minScore, cancellationToken);

        var factsTask = _longTerm.SearchFactsAsync(
            queryEmbedding, recallOpts.MaxFacts, minScore, cancellationToken);

        var tracesTask = _reasoning.SearchSimilarTracesAsync(
            queryEmbedding, null, recallOpts.MaxTraces, minScore, cancellationToken);

        Task<GraphRagContextResult?>? graphRagTask = null;
        if (_graphRag != null && _options.EnableGraphRag)
        {
            graphRagTask = FetchGraphRagAsync(request, recallOpts, cancellationToken);
        }

        await Task.WhenAll(
            recentTask, relevantTask, entitiesTask,
            preferencesTask, factsTask, tracesTask);

        if (graphRagTask != null)
            await graphRagTask;

        var recentMessages = await recentTask;
        var relevantMessages = await relevantTask;
        var entities = await entitiesTask;
        var preferences = await preferencesTask;
        var facts = await factsTask;
        var traces = await tracesTask;

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
            GraphRagContext = graphRagContext
        };

        _logger.LogDebug(
            "Assembled context for session {SessionId}: {Chars} chars (~{Tokens} tokens), truncated={Truncated}",
            request.SessionId, estimatedChars, estimatedChars / 4, truncated);

        return context;
    }

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

    private (
        IReadOnlyList<Message> recent,
        IReadOnlyList<Message> relevant,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<Preference> preferences,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<ReasoningTrace> traces,
        string? graphRag,
        bool truncated) ApplyBudget(
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
            return (recent, relevant, entities, preferences, facts, traces, graphRagContext, false);

        return budget.TruncationStrategy switch
        {
            TruncationStrategy.Fail =>
                throw MemoryError.Create($"Context budget exceeded: {totalChars} chars (limit {maxChars}).")
                    .WithCode(MemoryErrorCodes.ContextBudgetExceeded)
                    .WithMetadata("totalChars", totalChars)
                    .WithMetadata("maxChars", maxChars)
                    .Build(),

            TruncationStrategy.OldestFirst =>
                TruncateOldestFirst(maxChars, recent, relevant, entities, preferences, facts, traces, graphRagContext),

            TruncationStrategy.LowestScoreFirst =>
                TruncateLowestScoreFirst(maxChars, recent, relevant, entities, preferences, facts, traces, graphRagContext),

            TruncationStrategy.Proportional =>
                TruncateProportional(maxChars, totalChars, recent, relevant, entities, preferences, facts, traces, graphRagContext),

            _ => TruncateOldestFirst(maxChars, recent, relevant, entities, preferences, facts, traces, graphRagContext)
        };
    }

    private static (
        IReadOnlyList<Message>, IReadOnlyList<Message>, IReadOnlyList<Entity>,
        IReadOnlyList<Preference>, IReadOnlyList<Fact>, IReadOnlyList<ReasoningTrace>,
        string?, bool) TruncateOldestFirst(
        int maxChars,
        IReadOnlyList<Message> recent,
        IReadOnlyList<Message> relevant,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<Preference> preferences,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<ReasoningTrace> traces,
        string? graphRag)
    {
        // Order by newest first, keep items until we run out of budget
        var sortedRecent = recent.OrderByDescending(m => m.TimestampUtc).ToList();
        var sortedRelevant = relevant.OrderByDescending(m => m.TimestampUtc).ToList();
        var sortedTraces = traces.OrderByDescending(t => t.StartedAtUtc).ToList();
        var sortedEntities = entities.OrderByDescending(e => e.CreatedAtUtc).ToList();
        var sortedPreferences = preferences.OrderByDescending(p => p.CreatedAtUtc).ToList();
        var sortedFacts = facts.OrderByDescending(f => f.CreatedAtUtc).ToList();

        return FitWithinBudget(maxChars,
            sortedRecent, sortedRelevant, sortedEntities,
            sortedPreferences, sortedFacts, sortedTraces, graphRag);
    }

    private static (
        IReadOnlyList<Message>, IReadOnlyList<Message>, IReadOnlyList<Entity>,
        IReadOnlyList<Preference>, IReadOnlyList<Fact>, IReadOnlyList<ReasoningTrace>,
        string?, bool) TruncateLowestScoreFirst(
        int maxChars,
        IReadOnlyList<Message> recent,
        IReadOnlyList<Message> relevant,
        IReadOnlyList<Entity> entities,
        IReadOnlyList<Preference> preferences,
        IReadOnlyList<Fact> facts,
        IReadOnlyList<ReasoningTrace> traces,
        string? graphRag)
    {
        // Items are assumed to be ordered best-score-first from the repo
        // Trim from the end of each list iteratively
        return FitWithinBudget(maxChars,
            recent.ToList(), relevant.ToList(), entities.ToList(),
            preferences.ToList(), facts.ToList(), traces.ToList(), graphRag);
    }

    private static (
        IReadOnlyList<Message>, IReadOnlyList<Message>, IReadOnlyList<Entity>,
        IReadOnlyList<Preference>, IReadOnlyList<Fact>, IReadOnlyList<ReasoningTrace>,
        string?, bool) TruncateProportional(
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

        return (trimmedRecent, trimmedRelevant, trimmedEntities,
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

    private static (
        IReadOnlyList<Message>, IReadOnlyList<Message>, IReadOnlyList<Entity>,
        IReadOnlyList<Preference>, IReadOnlyList<Fact>, IReadOnlyList<ReasoningTrace>,
        string?, bool) FitWithinBudget(
        int maxChars,
        List<Message> recent,
        List<Message> relevant,
        List<Entity> entities,
        List<Preference> preferences,
        List<Fact> facts,
        List<ReasoningTrace> traces,
        string? graphRag)
    {
        // Remove items from the end of each section in round-robin until within budget
        int totalChars = EstimateChars(recent) + EstimateChars(relevant)
            + EstimateChars(entities) + EstimateChars(preferences)
            + EstimateChars(facts) + EstimateChars(traces)
            + (graphRag?.Length ?? 0);

        while (totalChars > maxChars)
        {
            bool removed = false;

            if (facts.Count > 0) { totalChars -= EstimateItemChars(facts[^1]); facts.RemoveAt(facts.Count - 1); removed = true; }
            else if (entities.Count > 0) { totalChars -= EstimateItemChars(entities[^1]); entities.RemoveAt(entities.Count - 1); removed = true; }
            else if (relevant.Count > 0) { totalChars -= EstimateItemChars(relevant[^1]); relevant.RemoveAt(relevant.Count - 1); removed = true; }
            else if (traces.Count > 0) { totalChars -= EstimateItemChars(traces[^1]); traces.RemoveAt(traces.Count - 1); removed = true; }
            else if (preferences.Count > 0) { totalChars -= EstimateItemChars(preferences[^1]); preferences.RemoveAt(preferences.Count - 1); removed = true; }
            else if (recent.Count > 0) { totalChars -= EstimateItemChars(recent[^1]); recent.RemoveAt(recent.Count - 1); removed = true; }
            else if (graphRag != null) { graphRag = null; totalChars -= graphRag?.Length ?? 0; removed = true; }

            if (!removed) break;
        }

        return (recent, relevant, entities, preferences, facts, traces, graphRag, true);
    }

    private static int EstimateChars<T>(IReadOnlyList<T> items) =>
        items.Sum(EstimateItemChars);

    private static int EstimateItemChars<T>(T item) => item switch
    {
        Message m => m.Content.Length,
        Entity e => (e.Name?.Length ?? 0) + (e.Description?.Length ?? 0) + 10,
        Fact f => f.Subject.Length + f.Predicate.Length + f.Object.Length + 4,
        Preference p => p.PreferenceText.Length,
        ReasoningTrace t => t.Task.Length + (t.Outcome?.Length ?? 0) + 10,
        _ => 50
    };
}
