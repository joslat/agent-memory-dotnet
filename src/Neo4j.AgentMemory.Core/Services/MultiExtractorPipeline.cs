using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Extraction pipeline that runs ALL registered extractors for each type in parallel,
/// then merges results using the configured <see cref="MergeStrategyType"/>.
/// Falls back gracefully when only one extractor is registered (no merge needed).
/// </summary>
public sealed class MultiExtractorPipeline : IMemoryExtractionPipeline
{
    private readonly IReadOnlyList<IEntityExtractor> _entityExtractors;
    private readonly IReadOnlyList<IFactExtractor> _factExtractors;
    private readonly IReadOnlyList<IPreferenceExtractor> _preferenceExtractors;
    private readonly IReadOnlyList<IRelationshipExtractor> _relationshipExtractors;
    private readonly ExtractionOptions _options;
    private readonly ILogger<MultiExtractorPipeline> _logger;

    public MultiExtractorPipeline(
        IEnumerable<IEntityExtractor> entityExtractors,
        IEnumerable<IFactExtractor> factExtractors,
        IEnumerable<IPreferenceExtractor> preferenceExtractors,
        IEnumerable<IRelationshipExtractor> relationshipExtractors,
        IOptions<ExtractionOptions> extractionOptions,
        ILogger<MultiExtractorPipeline> logger)
    {
        _entityExtractors = entityExtractors.ToList().AsReadOnly();
        _factExtractors = factExtractors.ToList().AsReadOnly();
        _preferenceExtractors = preferenceExtractors.ToList().AsReadOnly();
        _relationshipExtractors = relationshipExtractors.ToList().AsReadOnly();
        _options = extractionOptions.Value;
        _logger = logger;
    }

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        var types = request.TypesToExtract;
        var messages = request.Messages;
        var strategy = _options.MergeStrategy;

        _logger.LogDebug(
            "MultiExtractorPipeline starting extraction for session {SessionId} with merge strategy {Strategy}. " +
            "Extractors: {EntityCount} entity, {FactCount} fact, {PrefCount} preference, {RelCount} relationship.",
            request.SessionId, strategy,
            _entityExtractors.Count, _factExtractors.Count,
            _preferenceExtractors.Count, _relationshipExtractors.Count);

        // Run all extractor types in parallel.
        var entitiesTask = types.HasFlag(ExtractionTypes.Entities)
            ? RunExtractorsAsync(_entityExtractors, e => e.ExtractAsync(messages, cancellationToken), strategy, MergeStrategyFactory.CreateEntityStrategy, "entity", cancellationToken)
            : Task.FromResult<IReadOnlyList<ExtractedEntity>>(Array.Empty<ExtractedEntity>());

        var factsTask = types.HasFlag(ExtractionTypes.Facts)
            ? RunExtractorsAsync(_factExtractors, f => f.ExtractAsync(messages, cancellationToken), strategy, MergeStrategyFactory.CreateFactStrategy, "fact", cancellationToken)
            : Task.FromResult<IReadOnlyList<ExtractedFact>>(Array.Empty<ExtractedFact>());

        var prefsTask = types.HasFlag(ExtractionTypes.Preferences)
            ? RunExtractorsAsync(_preferenceExtractors, p => p.ExtractAsync(messages, cancellationToken), strategy, MergeStrategyFactory.CreatePreferenceStrategy, "preference", cancellationToken)
            : Task.FromResult<IReadOnlyList<ExtractedPreference>>(Array.Empty<ExtractedPreference>());

        var relsTask = types.HasFlag(ExtractionTypes.Relationships)
            ? RunExtractorsAsync(_relationshipExtractors, r => r.ExtractAsync(messages, cancellationToken), strategy, MergeStrategyFactory.CreateRelationshipStrategy, "relationship", cancellationToken)
            : Task.FromResult<IReadOnlyList<ExtractedRelationship>>(Array.Empty<ExtractedRelationship>());

        await Task.WhenAll(entitiesTask, factsTask, prefsTask, relsTask);

        var entities = await entitiesTask;
        var facts = await factsTask;
        var preferences = await prefsTask;
        var relationships = await relsTask;

        _logger.LogInformation(
            "MultiExtractorPipeline completed for session {SessionId}: " +
            "{EntityCount} entities, {FactCount} facts, {PrefCount} preferences, {RelCount} relationships.",
            request.SessionId, entities.Count, facts.Count, preferences.Count, relationships.Count);

        return new ExtractionResult
        {
            Entities = entities,
            Facts = facts,
            Preferences = preferences,
            Relationships = relationships,
            SourceMessageIds = request.Messages.Select(m => m.MessageId).ToList(),
            Metadata = new Dictionary<string, object>
            {
                ["sessionId"] = request.SessionId,
                ["mergeStrategy"] = strategy.ToString(),
                ["entityExtractorCount"] = _entityExtractors.Count,
                ["factExtractorCount"] = _factExtractors.Count,
                ["preferenceExtractorCount"] = _preferenceExtractors.Count,
                ["relationshipExtractorCount"] = _relationshipExtractors.Count
            }
        };
    }

    private async Task<IReadOnlyList<T>> RunExtractorsAsync<TExtractor, T>(
        IReadOnlyList<TExtractor> extractors,
        Func<TExtractor, Task<IReadOnlyList<T>>> extractFn,
        MergeStrategyType strategyType,
        Func<MergeStrategyType, IMergeStrategy<T>> strategyFactory,
        string extractorTypeName,
        CancellationToken cancellationToken) where T : class
    {
        if (extractors.Count == 0)
            return Array.Empty<T>();

        // Single extractor — no merge needed.
        if (extractors.Count == 1)
        {
            return await ExtractSafeAsync(
                () => extractFn(extractors[0]), extractorTypeName);
        }

        // Run all extractors in parallel.
        var tasks = extractors.Select(extractor =>
            ExtractSafeAsync(() => extractFn(extractor), extractorTypeName)).ToList();

        await Task.WhenAll(tasks);

        var allResults = new List<IReadOnlyList<T>>(tasks.Count);
        foreach (var task in tasks)
        {
            allResults.Add(await task);
        }

        var mergeStrategy = strategyFactory(strategyType);
        var merged = mergeStrategy.Merge(allResults);

        _logger.LogDebug(
            "Merged {InputCount} {Type} extractor results ({Strategy}): {ResultCount} items.",
            allResults.Count, extractorTypeName, strategyType, merged.Count);

        return merged;
    }

    private async Task<IReadOnlyList<T>> ExtractSafeAsync<T>(
        Func<Task<IReadOnlyList<T>>> extractor,
        string extractorType)
    {
        try
        {
            return await extractor();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Extractor for {ExtractorType} threw an exception — continuing with empty list.",
                extractorType);
            return Array.Empty<T>();
        }
    }
}
