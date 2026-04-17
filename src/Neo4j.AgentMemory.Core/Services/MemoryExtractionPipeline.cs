using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Core.Validation;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Production extraction pipeline: runs extractors in parallel, validates and resolves
/// entities, generates embeddings, then persists everything through repositories.
/// </summary>
public sealed class MemoryExtractionPipeline : IMemoryExtractionPipeline
{
    private readonly IEntityExtractor _entityExtractor;
    private readonly IFactExtractor _factExtractor;
    private readonly IPreferenceExtractor _preferenceExtractor;
    private readonly IRelationshipExtractor _relationshipExtractor;
    private readonly IEntityResolver _entityResolver;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IRelationshipRepository _relationshipRepository;
    private readonly ExtractionOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<MemoryExtractionPipeline> _logger;

    public MemoryExtractionPipeline(
        IEntityExtractor entityExtractor,
        IFactExtractor factExtractor,
        IPreferenceExtractor preferenceExtractor,
        IRelationshipExtractor relationshipExtractor,
        IEntityResolver entityResolver,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IEntityRepository entityRepository,
        IFactRepository factRepository,
        IPreferenceRepository preferenceRepository,
        IRelationshipRepository relationshipRepository,
        IOptions<ExtractionOptions> extractionOptions,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<MemoryExtractionPipeline> logger)
    {
        _entityExtractor = entityExtractor;
        _factExtractor = factExtractor;
        _preferenceExtractor = preferenceExtractor;
        _relationshipExtractor = relationshipExtractor;
        _entityResolver = entityResolver;
        _embeddingOrchestrator = embeddingOrchestrator;
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _preferenceRepository = preferenceRepository;
        _relationshipRepository = relationshipRepository;
        _options = extractionOptions.Value;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    public async Task<ExtractionResult> ExtractAsync(
        ExtractionRequest request,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogDebug(
            "Starting extraction for session {SessionId}, {MessageCount} messages.",
            request.SessionId, request.Messages.Count);

        var sourceMessageIds = request.Messages.Select(m => m.MessageId).ToList();
        var types = request.TypesToExtract;

        // 1. Run all enabled extractors in parallel, swallowing individual failures.
        var entityTask = types.HasFlag(ExtractionTypes.Entities)
            ? ExtractSafeAsync(
                () => _entityExtractor.ExtractAsync(request.Messages, cancellationToken),
                "entities",
                Array.Empty<ExtractedEntity>())
            : Task.FromResult<IReadOnlyList<ExtractedEntity>>(Array.Empty<ExtractedEntity>());

        var factTask = types.HasFlag(ExtractionTypes.Facts)
            ? ExtractSafeAsync(
                () => _factExtractor.ExtractAsync(request.Messages, cancellationToken),
                "facts",
                Array.Empty<ExtractedFact>())
            : Task.FromResult<IReadOnlyList<ExtractedFact>>(Array.Empty<ExtractedFact>());

        var preferenceTask = types.HasFlag(ExtractionTypes.Preferences)
            ? ExtractSafeAsync(
                () => _preferenceExtractor.ExtractAsync(request.Messages, cancellationToken),
                "preferences",
                Array.Empty<ExtractedPreference>())
            : Task.FromResult<IReadOnlyList<ExtractedPreference>>(Array.Empty<ExtractedPreference>());

        var relationshipTask = types.HasFlag(ExtractionTypes.Relationships)
            ? ExtractSafeAsync(
                () => _relationshipExtractor.ExtractAsync(request.Messages, cancellationToken),
                "relationships",
                Array.Empty<ExtractedRelationship>())
            : Task.FromResult<IReadOnlyList<ExtractedRelationship>>(Array.Empty<ExtractedRelationship>());

        await Task.WhenAll(entityTask, factTask, preferenceTask, relationshipTask);

        var extractedEntities = await entityTask;
        var extractedFacts = await factTask;
        var extractedPreferences = await preferenceTask;
        var extractedRelationships = await relationshipTask;

        // 2. Process entities: filter → validate → resolve → embed → persist.
        // Build a name→Entity map for relationship resolution in step 5.
        var resolvedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var extracted in extractedEntities)
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

                if (entity.Embedding is null)
                {
                    var embedding = await _embeddingOrchestrator.EmbedEntityAsync(entity.Name, cancellationToken);
                    entity = entity with { Embedding = embedding };
                }

                entity = await _entityRepository.UpsertAsync(entity, cancellationToken);
                resolvedEntityMap[extracted.Name] = entity;

                // Wire EXTRACTED_FROM relationships for provenance.
                foreach (var msgId in sourceMessageIds)
                {
                    try
                    {
                        await _entityRepository.CreateExtractedFromRelationshipAsync(
                            entity.EntityId, msgId, cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to create EXTRACTED_FROM for entity '{Id}' → message '{MsgId}'.",
                            entity.EntityId, msgId);
                    }
                }

                _logger.LogDebug("Persisted entity '{Name}' (id={Id}).", entity.Name, entity.EntityId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing entity '{Name}'.", extracted.Name);
            }
        }

        // 3. Process facts: filter → map → embed → persist.
        var persistedFactCount = 0;
        foreach (var extracted in extractedFacts)
        {
            if (extracted.Confidence < _options.MinConfidenceThreshold)
            {
                _logger.LogDebug(
                    "Skipping fact '{Subject} {Predicate} {Object}' — confidence {Confidence} below threshold.",
                    extracted.Subject, extracted.Predicate, extracted.Object, extracted.Confidence);
                continue;
            }

            try
            {
                var factEmbedding = await _embeddingOrchestrator.EmbedFactAsync(extracted.Subject, extracted.Predicate, extracted.Object, cancellationToken);

                var fact = new Fact
                {
                    FactId = _idGenerator.GenerateId(),
                    Subject = extracted.Subject,
                    Predicate = extracted.Predicate,
                    Object = extracted.Object,
                    Confidence = extracted.Confidence,
                    ValidFrom = extracted.ValidFrom,
                    ValidUntil = extracted.ValidUntil,
                    Embedding = factEmbedding,
                    SourceMessageIds = sourceMessageIds,
                    CreatedAtUtc = _clock.UtcNow
                };

                await _factRepository.UpsertAsync(fact, cancellationToken);

                // Wire EXTRACTED_FROM relationships for provenance.
                foreach (var msgId in sourceMessageIds)
                {
                    try
                    {
                        await _factRepository.CreateExtractedFromRelationshipAsync(
                            fact.FactId, msgId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to create EXTRACTED_FROM for fact '{Id}' → message '{MsgId}'.",
                            fact.FactId, msgId);
                    }
                }

                persistedFactCount++;
                _logger.LogDebug(
                    "Persisted fact '{Subject} {Predicate} {Object}'.",
                    fact.Subject, fact.Predicate, fact.Object);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing fact '{Subject} {Predicate} {Object}'.",
                    extracted.Subject, extracted.Predicate, extracted.Object);
            }
        }

        // 4. Process preferences: filter → map → embed → persist.
        var persistedPrefCount = 0;
        foreach (var extracted in extractedPreferences)
        {
            if (extracted.Confidence < _options.MinConfidenceThreshold)
            {
                _logger.LogDebug(
                    "Skipping preference '{Text}' — confidence {Confidence} below threshold.",
                    extracted.PreferenceText, extracted.Confidence);
                continue;
            }

            try
            {
                var prefEmbedding = await _embeddingOrchestrator.EmbedPreferenceAsync(extracted.PreferenceText, cancellationToken);

                var preference = new Preference
                {
                    PreferenceId = _idGenerator.GenerateId(),
                    Category = extracted.Category,
                    PreferenceText = extracted.PreferenceText,
                    Context = extracted.Context,
                    Confidence = extracted.Confidence,
                    Embedding = prefEmbedding,
                    SourceMessageIds = sourceMessageIds,
                    CreatedAtUtc = _clock.UtcNow
                };

                await _preferenceRepository.UpsertAsync(preference, cancellationToken);

                // Wire EXTRACTED_FROM relationships for provenance.
                foreach (var msgId in sourceMessageIds)
                {
                    try
                    {
                        await _preferenceRepository.CreateExtractedFromRelationshipAsync(
                            preference.PreferenceId, msgId, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to create EXTRACTED_FROM for preference '{Id}' → message '{MsgId}'.",
                            preference.PreferenceId, msgId);
                    }
                }

                persistedPrefCount++;
                _logger.LogDebug("Persisted preference in category '{Category}'.", preference.Category);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing preference '{Text}'.", extracted.PreferenceText);
            }
        }

        // 5. Process relationships: filter → resolve entity IDs → persist.
        var persistedRelCount = 0;
        foreach (var extracted in extractedRelationships)
        {
            if (extracted.Confidence < _options.MinConfidenceThreshold)
            {
                _logger.LogDebug(
                    "Skipping relationship '{Source}->{Target}' — confidence {Confidence} below threshold.",
                    extracted.SourceEntity, extracted.TargetEntity, extracted.Confidence);
                continue;
            }

            if (!resolvedEntityMap.TryGetValue(extracted.SourceEntity, out var sourceEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — source entity '{Source}' was not extracted or persisted.",
                    extracted.SourceEntity);
                continue;
            }

            if (!resolvedEntityMap.TryGetValue(extracted.TargetEntity, out var targetEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — target entity '{Target}' was not extracted or persisted.",
                    extracted.TargetEntity);
                continue;
            }

            try
            {
                var relationship = new Relationship
                {
                    RelationshipId = _idGenerator.GenerateId(),
                    SourceEntityId = sourceEntity.EntityId,
                    TargetEntityId = targetEntity.EntityId,
                    RelationshipType = extracted.RelationshipType,
                    Description = extracted.Description,
                    Confidence = extracted.Confidence,
                    Attributes = extracted.Attributes,
                    SourceMessageIds = sourceMessageIds,
                    CreatedAtUtc = _clock.UtcNow
                };

                await _relationshipRepository.UpsertAsync(relationship, cancellationToken);
                persistedRelCount++;
                _logger.LogDebug(
                    "Persisted relationship '{Source}-{Type}->{Target}'.",
                    extracted.SourceEntity, extracted.RelationshipType, extracted.TargetEntity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error processing relationship '{Source}->{Target}'.",
                    extracted.SourceEntity, extracted.TargetEntity);
            }
        }

        sw.Stop();
        _logger.LogInformation(
            "Extraction complete for session {SessionId}: {EntityCount} entities, {FactCount} facts, " +
            "{PrefCount} preferences, {RelCount} relationships in {ElapsedMs}ms.",
            request.SessionId,
            resolvedEntityMap.Count,
            persistedFactCount,
            persistedPrefCount,
            persistedRelCount,
            sw.ElapsedMilliseconds);

        return new ExtractionResult
        {
            Entities = extractedEntities,
            Facts = extractedFacts,
            Preferences = extractedPreferences,
            Relationships = extractedRelationships,
            SourceMessageIds = sourceMessageIds,
            Metadata = new Dictionary<string, object>
            {
                ["sessionId"] = request.SessionId,
                ["extractionTimeMs"] = sw.ElapsedMilliseconds,
                ["entityCount"] = resolvedEntityMap.Count,
                ["factCount"] = persistedFactCount,
                ["preferenceCount"] = persistedPrefCount,
                ["relationshipCount"] = persistedRelCount
            }
        };
    }

    private async Task<IReadOnlyList<T>> ExtractSafeAsync<T>(
        Func<Task<IReadOnlyList<T>>> extractor,
        string extractorType,
        IReadOnlyList<T> fallback)
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
            return fallback;
        }
    }
}
