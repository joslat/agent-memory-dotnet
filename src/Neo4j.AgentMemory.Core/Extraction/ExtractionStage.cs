using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Extraction.MergeStrategies;
using Neo4j.AgentMemory.Core.Validation;

namespace Neo4j.AgentMemory.Core.Extraction;

/// <summary>
/// Runs all registered extractors in parallel, merges results via the configured strategy,
/// applies confidence filtering + entity validation, and resolves entities against the graph.
/// Absorbs the functionality of the former <c>MultiExtractorPipeline</c>.
/// </summary>
internal sealed class ExtractionStage : IExtractionStage
{
    private readonly IReadOnlyList<IEntityExtractor> _entityExtractors;
    private readonly IReadOnlyList<IFactExtractor> _factExtractors;
    private readonly IReadOnlyList<IPreferenceExtractor> _preferenceExtractors;
    private readonly IReadOnlyList<IRelationshipExtractor> _relationshipExtractors;
    private readonly IEntityResolver _entityResolver;
    private readonly ExtractionOptions _options;
    private readonly ILogger<ExtractionStage> _logger;

    public ExtractionStage(
        IEnumerable<IEntityExtractor> entityExtractors,
        IEnumerable<IFactExtractor> factExtractors,
        IEnumerable<IPreferenceExtractor> preferenceExtractors,
        IEnumerable<IRelationshipExtractor> relationshipExtractors,
        IEntityResolver entityResolver,
        IOptions<ExtractionOptions> extractionOptions,
        ILogger<ExtractionStage> logger)
    {
        _entityExtractors = entityExtractors.ToList().AsReadOnly();
        _factExtractors = factExtractors.ToList().AsReadOnly();
        _preferenceExtractors = preferenceExtractors.ToList().AsReadOnly();
        _relationshipExtractors = relationshipExtractors.ToList().AsReadOnly();
        _entityResolver = entityResolver;
        _options = extractionOptions.Value;
        _logger = logger;
    }

    public async Task<ExtractionStageResult> ExtractAsync(
        IReadOnlyList<Message> messages,
        ExtractionTypes typesToExtract,
        CancellationToken cancellationToken = default)
    {
        var sourceMessageIds = messages.Select(m => m.MessageId).ToList();
        var strategy = _options.MergeStrategy;

        _logger.LogDebug(
            "ExtractionStage starting. Extractors: {E} entity, {F} fact, {P} preference, {R} relationship. Strategy: {Strategy}.",
            _entityExtractors.Count, _factExtractors.Count,
            _preferenceExtractors.Count, _relationshipExtractors.Count, strategy);

        // 1. Run all enabled extractor types in parallel.
        var entityTask = typesToExtract.HasFlag(ExtractionTypes.Entities)
            ? RunExtractorsAsync(_entityExtractors, e => e.ExtractAsync(messages, cancellationToken),
                strategy, MergeStrategyFactory.CreateEntityStrategy, "entity", cancellationToken)
            : Task.FromResult<IReadOnlyList<ExtractedEntity>>(Array.Empty<ExtractedEntity>());

        var factTask = typesToExtract.HasFlag(ExtractionTypes.Facts)
            ? RunExtractorsAsync(_factExtractors, f => f.ExtractAsync(messages, cancellationToken),
                strategy, MergeStrategyFactory.CreateFactStrategy, "fact", cancellationToken)
            : Task.FromResult<IReadOnlyList<ExtractedFact>>(Array.Empty<ExtractedFact>());

        var prefTask = typesToExtract.HasFlag(ExtractionTypes.Preferences)
            ? RunExtractorsAsync(_preferenceExtractors, p => p.ExtractAsync(messages, cancellationToken),
                strategy, MergeStrategyFactory.CreatePreferenceStrategy, "preference", cancellationToken)
            : Task.FromResult<IReadOnlyList<ExtractedPreference>>(Array.Empty<ExtractedPreference>());

        var relTask = typesToExtract.HasFlag(ExtractionTypes.Relationships)
            ? RunExtractorsAsync(_relationshipExtractors, r => r.ExtractAsync(messages, cancellationToken),
                strategy, MergeStrategyFactory.CreateRelationshipStrategy, "relationship", cancellationToken)
            : Task.FromResult<IReadOnlyList<ExtractedRelationship>>(Array.Empty<ExtractedRelationship>());

        await Task.WhenAll(entityTask, factTask, prefTask, relTask);

        var rawEntities = await entityTask;
        var rawFacts = await factTask;
        var rawPreferences = await prefTask;
        var rawRelationships = await relTask;

        // 2. Filter + validate + resolve entities; build name→Entity map for relationship resolution.
        var resolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var extracted in rawEntities)
        {
            if (extracted.Confidence < _options.MinConfidenceThreshold)
            {
                _logger.LogDebug(
                    "Skipping entity '{Name}' — confidence {Confidence} below threshold {Threshold}.",
                    extracted.Name, extracted.Confidence, _options.MinConfidenceThreshold);
                continue;
            }

            if (!EntityValidator.IsValid(extracted, _options.Validation))
            {
                _logger.LogWarning("Skipping entity '{Name}' — failed validation.", extracted.Name);
                continue;
            }

            try
            {
                var entity = await _entityResolver.ResolveEntityAsync(
                    extracted, sourceMessageIds, cancellationToken);
                resolvedEntityMap[extracted.Name] = entity;
                _logger.LogDebug("Resolved entity '{Name}' (id={Id}).", entity.Name, entity.EntityId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving entity '{Name}'.", extracted.Name);
            }
        }

        // 3. Confidence-filter facts.
        var filteredFacts = rawFacts
            .Where(f =>
            {
                if (f.Confidence >= _options.MinConfidenceThreshold) return true;
                _logger.LogDebug(
                    "Skipping fact '{S} {P} {O}' — confidence {C} below threshold.",
                    f.Subject, f.Predicate, f.Object, f.Confidence);
                return false;
            })
            .ToList();

        // 4. Confidence-filter preferences.
        var filteredPrefs = rawPreferences
            .Where(p =>
            {
                if (p.Confidence >= _options.MinConfidenceThreshold) return true;
                _logger.LogDebug(
                    "Skipping preference '{T}' — confidence {C} below threshold.",
                    p.PreferenceText, p.Confidence);
                return false;
            })
            .ToList();

        // 5. Confidence-filter relationships AND verify both endpoints are resolved.
        var filteredRels = new List<ExtractedRelationship>();
        foreach (var extracted in rawRelationships)
        {
            if (extracted.Confidence < _options.MinConfidenceThreshold)
            {
                _logger.LogDebug(
                    "Skipping relationship '{Src}->{Tgt}' — confidence {C} below threshold.",
                    extracted.SourceEntity, extracted.TargetEntity, extracted.Confidence);
                continue;
            }

            if (!resolvedEntityMap.ContainsKey(extracted.SourceEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — source entity '{Source}' not resolved.",
                    extracted.SourceEntity);
                continue;
            }

            if (!resolvedEntityMap.ContainsKey(extracted.TargetEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — target entity '{Target}' not resolved.",
                    extracted.TargetEntity);
                continue;
            }

            filteredRels.Add(extracted);
        }

        _logger.LogDebug(
            "ExtractionStage complete: {E} resolved entities, {F} facts, {P} preferences, {R} relationships.",
            resolvedEntityMap.Count, filteredFacts.Count, filteredPrefs.Count, filteredRels.Count);

        return new ExtractionStageResult
        {
            RawEntities = rawEntities,
            RawFacts = rawFacts,
            RawPreferences = rawPreferences,
            RawRelationships = rawRelationships,
            ResolvedEntityMap = resolvedEntityMap,
            FilteredFacts = filteredFacts.AsReadOnly(),
            FilteredPreferences = filteredPrefs.AsReadOnly(),
            FilteredRelationships = filteredRels.AsReadOnly(),
            SourceMessageIds = sourceMessageIds,
            MergeStrategy = strategy,
            EntityExtractorCount = _entityExtractors.Count,
            FactExtractorCount = _factExtractors.Count,
            PreferenceExtractorCount = _preferenceExtractors.Count,
            RelationshipExtractorCount = _relationshipExtractors.Count
        };
    }

    // ── Multi-extractor runner (ported from MultiExtractorPipeline) ──

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

        if (extractors.Count == 1)
            return await ExtractSafeAsync(() => extractFn(extractors[0]), extractorTypeName);

        var tasks = extractors
            .Select(e => ExtractSafeAsync(() => extractFn(e), extractorTypeName))
            .ToList();
        await Task.WhenAll(tasks);

        var allResults = new List<IReadOnlyList<T>>(tasks.Count);
        foreach (var task in tasks)
            allResults.Add(await task);

        var mergeStrategy = strategyFactory(strategyType);
        var merged = mergeStrategy.Merge(allResults);

        _logger.LogDebug(
            "Merged {Count} {Type} extractor results ({Strategy}): {Result} items.",
            allResults.Count, extractorTypeName, strategyType, merged.Count);

        return merged;
    }

    private async Task<IReadOnlyList<T>> ExtractSafeAsync<T>(
        Func<Task<IReadOnlyList<T>>> extractor,
        string extractorTypeName)
    {
        try
        {
            return await extractor();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Extractor for {ExtractorType} threw — continuing with empty list.",
                extractorTypeName);
            return Array.Empty<T>();
        }
    }
}
