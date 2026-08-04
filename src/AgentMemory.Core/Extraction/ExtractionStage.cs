using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction.MergeStrategies;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Validation;

namespace AgentMemory.Core.Extraction;

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
    private readonly IReadOnlyList<IUnifiedMemoryExtractor> _unifiedExtractors;
    private readonly IEntityResolver _entityResolver;
    private readonly ExtractionOptions _options;
    private readonly ILogger<ExtractionStage> _logger;

    public ExtractionStage(
        IEnumerable<IEntityExtractor> entityExtractors,
        IEnumerable<IFactExtractor> factExtractors,
        IEnumerable<IPreferenceExtractor> preferenceExtractors,
        IEnumerable<IRelationshipExtractor> relationshipExtractors,
        IEnumerable<IUnifiedMemoryExtractor> unifiedExtractors,
        IEntityResolver entityResolver,
        IOptions<ExtractionOptions> extractionOptions,
        ILogger<ExtractionStage> logger)
    {
        _entityExtractors = entityExtractors.ToList().AsReadOnly();
        _factExtractors = factExtractors.ToList().AsReadOnly();
        _preferenceExtractors = preferenceExtractors.ToList().AsReadOnly();
        _relationshipExtractors = relationshipExtractors.ToList().AsReadOnly();
        _unifiedExtractors = unifiedExtractors.ToList().AsReadOnly();
        _entityResolver = entityResolver;
        _options = extractionOptions.Value;
        _logger = logger;
    }

    public IDisposable? BeginResolutionBatch() =>
        (_entityResolver as IExtractionEntityResolver)?.BeginBatch();

    public void InvalidateResolutionBatch() =>
        (_entityResolver as IExtractionEntityResolver)?.InvalidateBatch();

    public Task<ExtractionStageResult> ExtractAsync(
        IReadOnlyList<Message> messages,
        ExtractionTypes typesToExtract,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        ExtractCoreAsync(messages, typesToExtract, scope, preExtracted: null, cancellationToken);

    public Task<ExtractionStageResult> ProcessUnifiedAsync(
        IReadOnlyList<Message> messages,
        UnifiedExtractionResult extracted,
        ExtractionTypes typesToExtract,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(extracted);
        return ExtractCoreAsync(messages, typesToExtract, scope, extracted, cancellationToken);
    }

    private async Task<ExtractionStageResult> ExtractCoreAsync(
        IReadOnlyList<Message> messages,
        ExtractionTypes typesToExtract,
        MemoryScope? scope,
        UnifiedExtractionResult? preExtracted,
        CancellationToken cancellationToken)
    {
        var sourceMessageIds = messages.Select(m => m.MessageId).ToList();
        var strategy = _options.MergeStrategy;
        var failFast = _options.FailureMode == IngestionFailureMode.FailFast;

        _logger.LogDebug(
            "ExtractionStage starting. Extractors: {E} entity, {F} fact, {P} preference, {R} relationship. Strategy: {Strategy}.",
            _entityExtractors.Count, _factExtractors.Count,
            _preferenceExtractors.Count, _relationshipExtractors.Count, strategy);

        Task<(IReadOnlyList<ExtractedEntity> Items, IReadOnlyList<IngestionItemOutcome> Outcomes)> entityRun;
        Task<(IReadOnlyList<ExtractedFact> Items, IReadOnlyList<IngestionItemOutcome> Outcomes)> factRun;
        Task<(IReadOnlyList<ExtractedPreference> Items, IReadOnlyList<IngestionItemOutcome> Outcomes)> prefRun;
        Task<(IReadOnlyList<ExtractedRelationship> Items, IReadOnlyList<IngestionItemOutcome> Outcomes)> relRun;
        IReadOnlyList<IngestionItemOutcome> unifiedOutcomes = Array.Empty<IngestionItemOutcome>();
        var unifiedExtractor = typesToExtract != ExtractionTypes.None
            ? _unifiedExtractors.FirstOrDefault(extractor => extractor.IsEnabled)
            : null;
        if (preExtracted is not null)
        {
            entityRun = CompletedRun(typesToExtract.HasFlag(ExtractionTypes.Entities) ? preExtracted.Entities : []);
            factRun = CompletedRun(typesToExtract.HasFlag(ExtractionTypes.Facts) ? preExtracted.Facts : []);
            prefRun = CompletedRun(typesToExtract.HasFlag(ExtractionTypes.Preferences) ? preExtracted.Preferences : []);
            relRun = CompletedRun(typesToExtract.HasFlag(ExtractionTypes.Relationships) ? preExtracted.Relationships : []);
        }
        else if (unifiedExtractor is not null)
        {
            var unifiedRun = await ExtractUnifiedSafeAsync(
                unifiedExtractor, messages, typesToExtract, cancellationToken).ConfigureAwait(false);
            var unified = unifiedRun.Result;
            unifiedOutcomes = unifiedRun.Outcomes;
            entityRun = CompletedRun(typesToExtract.HasFlag(ExtractionTypes.Entities) ? unified.Entities : []);
            factRun = CompletedRun(typesToExtract.HasFlag(ExtractionTypes.Facts) ? unified.Facts : []);
            prefRun = CompletedRun(typesToExtract.HasFlag(ExtractionTypes.Preferences) ? unified.Preferences : []);
            relRun = CompletedRun(typesToExtract.HasFlag(ExtractionTypes.Relationships) ? unified.Relationships : []);
        }
        else
        {
            entityRun = typesToExtract.HasFlag(ExtractionTypes.Entities)
                ? RunExtractorsAsync(_entityExtractors, e => e.ExtractAsync(messages, cancellationToken),
                    strategy, MergeStrategyFactory.CreateEntityStrategy, "entity",
                    MemoryItemKind.Entity, MemoryErrorCodes.EntityExtractionFailed, cancellationToken)
                : EmptyRun<ExtractedEntity>();
            factRun = typesToExtract.HasFlag(ExtractionTypes.Facts)
                ? RunExtractorsAsync(_factExtractors, f => f.ExtractAsync(messages, cancellationToken),
                    strategy, MergeStrategyFactory.CreateFactStrategy, "fact",
                    MemoryItemKind.Fact, MemoryErrorCodes.FactExtractionFailed, cancellationToken)
                : EmptyRun<ExtractedFact>();
            prefRun = typesToExtract.HasFlag(ExtractionTypes.Preferences)
                ? RunExtractorsAsync(_preferenceExtractors, p => p.ExtractAsync(messages, cancellationToken),
                    strategy, MergeStrategyFactory.CreatePreferenceStrategy, "preference",
                    MemoryItemKind.Preference, MemoryErrorCodes.PreferenceExtractionFailed, cancellationToken)
                : EmptyRun<ExtractedPreference>();
            relRun = typesToExtract.HasFlag(ExtractionTypes.Relationships)
                ? RunExtractorsAsync(_relationshipExtractors, r => r.ExtractAsync(messages, cancellationToken),
                    strategy, MergeStrategyFactory.CreateRelationshipStrategy, "relationship",
                    MemoryItemKind.Relationship, MemoryErrorCodes.RelationshipExtractionFailed, cancellationToken)
                : EmptyRun<ExtractedRelationship>();
        }

        await Task.WhenAll(entityRun, factRun, prefRun, relRun).ConfigureAwait(false);

        var (rawEntities, entityOutcomes) = await entityRun.ConfigureAwait(false);
        var (rawFacts, factOutcomes) = await factRun.ConfigureAwait(false);
        var (rawPreferences, prefOutcomes) = await prefRun.ConfigureAwait(false);
        var (rawRelationships, relOutcomes) = await relRun.ConfigureAwait(false);

        var outcomes = new List<IngestionItemOutcome>();
        outcomes.AddRange(unifiedOutcomes);
        outcomes.AddRange(entityOutcomes);
        outcomes.AddRange(factOutcomes);
        outcomes.AddRange(prefOutcomes);
        outcomes.AddRange(relOutcomes);

        // FailFast (#101): the four extractor categories above already ran concurrently to completion --
        // in-flight extractor tasks can't be pre-empted -- so "stop at the first failure" is honored at
        // the first point after that where sequential, stoppable work begins: here, and again inside the
        // per-entity resolution loop below.
        if (failFast && outcomes.Any(o => o.Status == IngestionItemStatus.Failed))
        {
            throw new MemoryIngestionException(
                "Ingestion failed fast: one or more extractors threw.", outcomes);
        }

        if (_entityResolver is IExtractionEntityResolver batchResolver)
        {
            var candidateTypes = rawEntities
                .Where(entity =>
                    entity.Confidence >= _options.MinConfidenceThreshold &&
                    EntityValidator.IsValid(entity, _options.Validation))
                .Select(entity => entity.Type)
                .ToArray();
            await batchResolver.PrepareCandidatesAsync(candidateTypes, scope, cancellationToken)
                .ConfigureAwait(false);
        }

        // 2. Filter + validate + resolve entities; build name→Entity map for relationship resolution.
        // Spanned separately from extraction: resolution is a SEQUENTIAL per-entity loop, so unlike the
        // concurrent extractor categories above its cost grows linearly with entity count.
        using var resolutionActivity = AgentMemoryDiagnostics.Source.StartActivity("memory.extract.resolution");
        resolutionActivity?.SetTag("memory.extract.candidate_entities", rawEntities.Count);
        resolutionActivity?.SetTag("memory.extract.source_messages", sourceMessageIds.Count);

        var resolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var extracted in rawEntities)
        {
            // Confidence-threshold filtering is routine, expected pipeline behavior, not a failure or a
            // meaningful skip -- deliberately NOT recorded as an outcome (#101 acceptance: "important
            // skips", not every filtered candidate).
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
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Entity,
                    Stage = IngestionStage.Validation,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = extracted.Name,
                    ErrorCode = MemoryErrorCodes.EntityValidationFailed,
                    ErrorMessage = "Entity candidate failed validation rules.",
                });
                continue;
            }

            try
            {
                var entity = failFast && _entityResolver is IExtractionEntityResolver deferredResolver
                    ? await deferredResolver.ResolveForPersistenceAsync(
                        extracted, sourceMessageIds, scope, cancellationToken).ConfigureAwait(false)
                    : await _entityResolver.ResolveEntityAsync(
                        extracted, sourceMessageIds, scope, cancellationToken).ConfigureAwait(false);
                resolvedEntityMap[extracted.Name] = entity;
                _logger.LogDebug("Resolved entity '{Name}' (id={Id}).", entity.Name, entity.EntityId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw; // Honor caller cancellation — do not mask it as a per-entity resolution error.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error resolving entity '{Name}'.", extracted.Name);
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Entity,
                    Stage = IngestionStage.Resolution,
                    Status = IngestionItemStatus.Failed,
                    SourceKey = extracted.Name,
                    ErrorCode = MemoryErrorCodes.EntityResolutionFailed,
                    ErrorMessage = ex.Message,
                    Retryable = true,
                });

                if (failFast)
                {
                    throw new MemoryIngestionException(
                        $"Ingestion failed fast: entity resolution failed for '{extracted.Name}'.", outcomes, ex);
                }
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
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.Resolution,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = $"{extracted.SourceEntity}->{extracted.TargetEntity}",
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointUnresolved,
                    ErrorMessage = $"Source entity '{extracted.SourceEntity}' was not resolved.",
                });
                continue;
            }

            if (!resolvedEntityMap.ContainsKey(extracted.TargetEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — target entity '{Target}' not resolved.",
                    extracted.TargetEntity);
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.Resolution,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = $"{extracted.SourceEntity}->{extracted.TargetEntity}",
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointUnresolved,
                    ErrorMessage = $"Target entity '{extracted.TargetEntity}' was not resolved.",
                });
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
            RelationshipExtractorCount = _relationshipExtractors.Count,
            Outcomes = outcomes
        };
    }

    // ── Multi-extractor runner (ported from MultiExtractorPipeline) ──

    private static Task<(IReadOnlyList<T> Items, IReadOnlyList<IngestionItemOutcome> Outcomes)> EmptyRun<T>() where T : class =>
        Task.FromResult<(IReadOnlyList<T>, IReadOnlyList<IngestionItemOutcome>)>(
            (Array.Empty<T>(), Array.Empty<IngestionItemOutcome>()));

    private static Task<(IReadOnlyList<T> Items, IReadOnlyList<IngestionItemOutcome> Outcomes)> CompletedRun<T>(
        IReadOnlyList<T> items) where T : class =>
        Task.FromResult((items, (IReadOnlyList<IngestionItemOutcome>)Array.Empty<IngestionItemOutcome>()));

    private async Task<(UnifiedExtractionResult Result, IReadOnlyList<IngestionItemOutcome> Outcomes)> ExtractUnifiedSafeAsync(
        IUnifiedMemoryExtractor extractor,
        IReadOnlyList<Message> messages,
        ExtractionTypes typesToExtract,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await extractor.ExtractAsync(messages, cancellationToken).ConfigureAwait(false),
                Array.Empty<IngestionItemOutcome>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unified memory extraction threw — continuing with empty results.");
            var outcomes = new List<IngestionItemOutcome>(4);
            AddUnifiedFailure(outcomes, typesToExtract, ExtractionTypes.Entities,
                MemoryItemKind.Entity, "entity", MemoryErrorCodes.EntityExtractionFailed, ex);
            AddUnifiedFailure(outcomes, typesToExtract, ExtractionTypes.Facts,
                MemoryItemKind.Fact, "fact", MemoryErrorCodes.FactExtractionFailed, ex);
            AddUnifiedFailure(outcomes, typesToExtract, ExtractionTypes.Preferences,
                MemoryItemKind.Preference, "preference", MemoryErrorCodes.PreferenceExtractionFailed, ex);
            AddUnifiedFailure(outcomes, typesToExtract, ExtractionTypes.Relationships,
                MemoryItemKind.Relationship, "relationship", MemoryErrorCodes.RelationshipExtractionFailed, ex);
            return (new UnifiedExtractionResult(), outcomes);
        }
    }

    private static void AddUnifiedFailure(
        ICollection<IngestionItemOutcome> outcomes,
        ExtractionTypes typesToExtract,
        ExtractionTypes requiredType,
        MemoryItemKind kind,
        string sourceKey,
        string errorCode,
        Exception exception)
    {
        if (!typesToExtract.HasFlag(requiredType))
            return;

        outcomes.Add(new IngestionItemOutcome
        {
            Kind = kind,
            Stage = IngestionStage.Extraction,
            Status = IngestionItemStatus.Failed,
            SourceKey = $"unified:{sourceKey}",
            ErrorCode = errorCode,
            ErrorMessage = exception.Message,
            Retryable = true,
        });
    }

    private async Task<(IReadOnlyList<T> Items, IReadOnlyList<IngestionItemOutcome> Outcomes)> RunExtractorsAsync<TExtractor, T>(
        IReadOnlyList<TExtractor> extractors,
        Func<TExtractor, Task<IReadOnlyList<T>>> extractFn,
        MergeStrategyType strategyType,
        Func<MergeStrategyType, IMergeStrategy<T>> strategyFactory,
        string extractorTypeName,
        MemoryItemKind kind,
        string failureErrorCode,
        CancellationToken cancellationToken) where T : class
    {
        if (extractors.Count == 0)
            return (Array.Empty<T>(), Array.Empty<IngestionItemOutcome>());

        // One span per extractor category. The four categories run concurrently, so these overlap —
        // which is exactly what makes them worth separating: without per-category attribution the only
        // observable is the slowest of the four, and there is no way to see that each is a separate
        // model completion carrying its own copy of the turn.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity($"memory.extract.{extractorTypeName}");
        activity?.SetTag("memory.extract.extractor_count", extractors.Count);

        if (extractors.Count == 1)
            return await ExtractSafeAsync(() => extractFn(extractors[0]), extractorTypeName, kind, failureErrorCode, cancellationToken)
                .ConfigureAwait(false);

        var tasks = extractors
            .Select(e => ExtractSafeAsync(() => extractFn(e), extractorTypeName, kind, failureErrorCode, cancellationToken))
            .ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);

        var allResults = new List<IReadOnlyList<T>>(tasks.Count);
        var allOutcomes = new List<IngestionItemOutcome>();
        foreach (var task in tasks)
        {
            var (items, outcomes) = await task.ConfigureAwait(false);
            allResults.Add(items);
            allOutcomes.AddRange(outcomes);
        }

        var mergeStrategy = strategyFactory(strategyType);
        var merged = mergeStrategy.Merge(allResults);

        _logger.LogDebug(
            "Merged {Count} {Type} extractor results ({Strategy}): {Result} items.",
            allResults.Count, extractorTypeName, strategyType, merged.Count);

        return (merged, allOutcomes);
    }

    private async Task<(IReadOnlyList<T> Items, IReadOnlyList<IngestionItemOutcome> Outcomes)> ExtractSafeAsync<T>(
        Func<Task<IReadOnlyList<T>>> extractor,
        string extractorTypeName,
        MemoryItemKind kind,
        string failureErrorCode,
        CancellationToken cancellationToken)
    {
        try
        {
            var items = await extractor().ConfigureAwait(false);
            return (items, Array.Empty<IngestionItemOutcome>());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Honor caller cancellation — do not mask it as an empty result.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Extractor for {ExtractorType} threw — continuing with empty list.",
                extractorTypeName);
            var outcome = new IngestionItemOutcome
            {
                Kind = kind,
                Stage = IngestionStage.Extraction,
                Status = IngestionItemStatus.Failed,
                SourceKey = extractorTypeName,
                ErrorCode = failureErrorCode,
                ErrorMessage = ex.Message,
                Retryable = true,
            };
            return (Array.Empty<T>(), new[] { outcome });
        }
    }
}
