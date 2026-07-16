using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// Embeds and persists the resolved items from <see cref="ExtractionStage"/>.
/// Responsibility: generate embeddings, upsert to repositories, wire EXTRACTED_FROM provenance.
/// </summary>
internal sealed class PersistenceStage : IPersistenceStage
{
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IPreferenceRepository _preferenceRepository;
    private readonly IRelationshipRepository _relationshipRepository;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ExtractionOptions _options;
    private readonly ILogger<PersistenceStage> _logger;

    public PersistenceStage(
        IEmbeddingOrchestrator embeddingOrchestrator,
        IEntityRepository entityRepository,
        IFactRepository factRepository,
        IPreferenceRepository preferenceRepository,
        IRelationshipRepository relationshipRepository,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<PersistenceStage> logger,
        IOptions<ExtractionOptions>? extractionOptions = null)
    {
        _embeddingOrchestrator = embeddingOrchestrator;
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _preferenceRepository = preferenceRepository;
        _relationshipRepository = relationshipRepository;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
        _options = extractionOptions?.Value ?? new ExtractionOptions();
    }

    public async Task<PersistenceResult> PersistAsync(
        ExtractionStageResult extraction,
        string? ownerId = null,
        MemoryTrustLevel trustLevel = MemoryTrustLevel.Untrusted,
        CancellationToken cancellationToken = default)
    {
        var sourceMessageIds = extraction.SourceMessageIds;
        var failFast = _options.FailureMode == IngestionFailureMode.FailFast;
        var outcomes = new List<IngestionItemOutcome>(extraction.Outcomes);

        // 1. Embed + upsert entities; build a name→persisted Entity map for relationship resolution.
        var persistedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entity) in extraction.ResolvedEntityMap)
        {
            // Trust is monotonic, never silently downgraded: when entity resolution (auto-merge/SAME_AS)
            // resolves this mention onto an EXISTING, previously-persisted entity, `entity` already carries
            // that entity's own prior Metadata/trust level. An unrelated later mention at a lower trust
            // level (e.g. an ordinary chat turn) must not erase a deliberately-elevated trust stamp (e.g.
            // from a curated ApplicationTrusted import) -- take whichever of the two is higher.
            var effectiveTrustLevel = MaxTrustLevel(entity.Metadata.GetTrustLevel(), trustLevel);
            var entityToSave = entity with { OwnerId = ownerId, Metadata = entity.Metadata.WithTrustLevel(effectiveTrustLevel) };

            if (entityToSave.Embedding is null)
            {
                try
                {
                    var embedding = await _embeddingOrchestrator.EmbedEntityAsync(
                        entityToSave.Name, cancellationToken).ConfigureAwait(false);
                    entityToSave = entityToSave with { Embedding = embedding };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error generating embedding for entity '{Name}'.", name);
                    RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Entity, IngestionStage.Embedding,
                        MemoryErrorCodes.EmbeddingGenerationFailed, name, null, ex,
                        $"Ingestion failed fast: embedding generation failed for entity '{name}'.");
                    continue; // no embedding — nothing to persist for this entity
                }
            }

            try
            {
                entityToSave = await _entityRepository.UpsertAsync(entityToSave, cancellationToken).ConfigureAwait(false);
                persistedEntityMap[name] = entityToSave;
                RecordSuccess(outcomes, MemoryItemKind.Entity, name, entityToSave.EntityId);

                foreach (var msgId in sourceMessageIds)
                {
                    try
                    {
                        await _entityRepository.CreateExtractedFromRelationshipAsync(
                            entityToSave.EntityId, msgId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to create EXTRACTED_FROM for entity '{Id}' → message '{MsgId}'.",
                            entityToSave.EntityId, msgId);
                        RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Entity, IngestionStage.Provenance,
                            MemoryErrorCodes.ProvenancePersistenceFailed, name, entityToSave.EntityId, ex,
                            $"Ingestion failed fast: provenance failed for entity '{name}'.");
                    }
                }

                _logger.LogDebug("Persisted entity '{Name}' (id={Id}).", entityToSave.Name, entityToSave.EntityId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; } // already recorded + wrapped above — propagate as-is
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting entity '{Name}'.", name);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Entity, IngestionStage.Persistence,
                    MemoryErrorCodes.EntityPersistenceFailed, name, null, ex,
                    $"Ingestion failed fast: persistence failed for entity '{name}'.");
            }
        }

        // 2. Embed + upsert facts.
        var persistedFactCount = 0;
        foreach (var extracted in extraction.FilteredFacts)
        {
            var factSourceKey = $"{extracted.Subject} {extracted.Predicate} {extracted.Object}";

            float[] factEmbedding;
            try
            {
                factEmbedding = await _embeddingOrchestrator.EmbedFactAsync(
                    extracted.Subject, extracted.Predicate, extracted.Object, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embedding for fact '{Key}'.", factSourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Embedding,
                    MemoryErrorCodes.EmbeddingGenerationFailed, factSourceKey, null, ex,
                    $"Ingestion failed fast: embedding generation failed for fact '{factSourceKey}'.");
                continue;
            }

            try
            {
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
                    OwnerId = ownerId,
                    SourceMessageIds = sourceMessageIds,
                    CreatedAtUtc = _clock.UtcNow,
                    Metadata = MemoryTrustMetadataExtensions.CreateWithTrustLevel(trustLevel)
                };

                await _factRepository.UpsertAsync(fact, cancellationToken).ConfigureAwait(false);
                RecordSuccess(outcomes, MemoryItemKind.Fact, factSourceKey, fact.FactId);

                foreach (var msgId in sourceMessageIds)
                {
                    try
                    {
                        await _factRepository.CreateExtractedFromRelationshipAsync(
                            fact.FactId, msgId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to create EXTRACTED_FROM for fact '{Id}' → message '{MsgId}'.",
                            fact.FactId, msgId);
                        RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Provenance,
                            MemoryErrorCodes.ProvenancePersistenceFailed, factSourceKey, fact.FactId, ex,
                            $"Ingestion failed fast: provenance failed for fact '{factSourceKey}'.");
                    }
                }

                persistedFactCount++;
                _logger.LogDebug("Persisted fact '{S} {P} {O}'.", fact.Subject, fact.Predicate, fact.Object);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting fact '{Key}'.", factSourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Persistence,
                    MemoryErrorCodes.FactPersistenceFailed, factSourceKey, null, ex,
                    $"Ingestion failed fast: persistence failed for fact '{factSourceKey}'.");
            }
        }

        // 3. Embed + upsert preferences.
        var persistedPrefCount = 0;
        foreach (var extracted in extraction.FilteredPreferences)
        {
            float[] prefEmbedding;
            try
            {
                prefEmbedding = await _embeddingOrchestrator.EmbedPreferenceAsync(
                    extracted.PreferenceText, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embedding for preference '{Text}'.", extracted.PreferenceText);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Embedding,
                    MemoryErrorCodes.EmbeddingGenerationFailed, extracted.PreferenceText, null, ex,
                    "Ingestion failed fast: embedding generation failed for a preference.");
                continue;
            }

            try
            {
                var preference = new Preference
                {
                    PreferenceId = _idGenerator.GenerateId(),
                    Category = extracted.Category,
                    PreferenceText = extracted.PreferenceText,
                    Context = extracted.Context,
                    Confidence = extracted.Confidence,
                    Embedding = prefEmbedding,
                    OwnerId = ownerId,
                    SourceMessageIds = sourceMessageIds,
                    CreatedAtUtc = _clock.UtcNow,
                    Metadata = MemoryTrustMetadataExtensions.CreateWithTrustLevel(trustLevel)
                };

                await _preferenceRepository.UpsertAsync(preference, cancellationToken).ConfigureAwait(false);
                RecordSuccess(outcomes, MemoryItemKind.Preference, extracted.PreferenceText, preference.PreferenceId);

                foreach (var msgId in sourceMessageIds)
                {
                    try
                    {
                        await _preferenceRepository.CreateExtractedFromRelationshipAsync(
                            preference.PreferenceId, msgId, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to create EXTRACTED_FROM for preference '{Id}' → message '{MsgId}'.",
                            preference.PreferenceId, msgId);
                        RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Provenance,
                            MemoryErrorCodes.ProvenancePersistenceFailed, extracted.PreferenceText, preference.PreferenceId, ex,
                            "Ingestion failed fast: provenance failed for a preference.");
                    }
                }

                persistedPrefCount++;
                _logger.LogDebug("Persisted preference in category '{Category}'.", preference.Category);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting preference '{Text}'.", extracted.PreferenceText);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Persistence,
                    MemoryErrorCodes.PreferencePersistenceFailed, extracted.PreferenceText, null, ex,
                    "Ingestion failed fast: persistence failed for a preference.");
            }
        }

        // 4. Persist relationships — resolve entity IDs from the upserted entity map.
        var persistedRelCount = 0;
        foreach (var extracted in extraction.FilteredRelationships)
        {
            var relSourceKey = $"{extracted.SourceEntity}-{extracted.RelationshipType}->{extracted.TargetEntity}";

            if (!persistedEntityMap.TryGetValue(extracted.SourceEntity, out var sourceEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — source entity '{Source}' was not persisted.",
                    extracted.SourceEntity);
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.RelationshipPersistence,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = relSourceKey,
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointNotPersisted,
                    ErrorMessage = $"Source entity '{extracted.SourceEntity}' was not persisted.",
                });
                continue;
            }

            if (!persistedEntityMap.TryGetValue(extracted.TargetEntity, out var targetEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — target entity '{Target}' was not persisted.",
                    extracted.TargetEntity);
                outcomes.Add(new IngestionItemOutcome
                {
                    Kind = MemoryItemKind.Relationship,
                    Stage = IngestionStage.RelationshipPersistence,
                    Status = IngestionItemStatus.Skipped,
                    SourceKey = relSourceKey,
                    ErrorCode = MemoryErrorCodes.RelationshipEndpointNotPersisted,
                    ErrorMessage = $"Target entity '{extracted.TargetEntity}' was not persisted.",
                });
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
                    OwnerId = ownerId,
                    SourceMessageIds = sourceMessageIds,
                    CreatedAtUtc = _clock.UtcNow
                };

                await _relationshipRepository.UpsertAsync(relationship, cancellationToken).ConfigureAwait(false);
                persistedRelCount++;
                RecordSuccess(outcomes, MemoryItemKind.Relationship, relSourceKey, relationship.RelationshipId);

                _logger.LogDebug(
                    "Persisted relationship '{Src}-{Type}->{Tgt}'.",
                    extracted.SourceEntity, extracted.RelationshipType, extracted.TargetEntity);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (MemoryIngestionException) { throw; } // consistent with the other item kinds (#101 review)
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error persisting relationship '{Src}->{Tgt}'.",
                    extracted.SourceEntity, extracted.TargetEntity);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Relationship, IngestionStage.Persistence,
                    MemoryErrorCodes.RelationshipPersistenceFailed, relSourceKey, null, ex,
                    $"Ingestion failed fast: persistence failed for relationship '{relSourceKey}'.");
            }
        }

        return new PersistenceResult
        {
            EntityCount = persistedEntityMap.Count,
            FactCount = persistedFactCount,
            PreferenceCount = persistedPrefCount,
            RelationshipCount = persistedRelCount,
            Outcomes = outcomes
        };
    }

    /// <summary>
    /// Trust is monotonic (#92 Phase 3): re-touching an already-persisted entity must never silently lower
    /// its trust level below whatever it already had.
    /// </summary>
    private static MemoryTrustLevel MaxTrustLevel(MemoryTrustLevel a, MemoryTrustLevel b) => a > b ? a : b;

    /// <summary>Appends a <see cref="IngestionItemStatus.Succeeded"/> outcome (#101).</summary>
    private static void RecordSuccess(
        List<IngestionItemOutcome> outcomes, MemoryItemKind kind, string? sourceKey, string? persistedId) =>
        outcomes.Add(new IngestionItemOutcome
        {
            Kind = kind,
            Stage = IngestionStage.Persistence,
            Status = IngestionItemStatus.Succeeded,
            SourceKey = sourceKey,
            PersistedId = persistedId,
        });

    /// <summary>
    /// Appends a <see cref="IngestionItemStatus.Failed"/> outcome and, under
    /// <see cref="IngestionFailureMode.FailFast"/>, throws <see cref="MemoryIngestionException"/>
    /// carrying every outcome recorded so far (#101). Centralizing this in one helper — rather than
    /// repeating "add outcome, then maybe throw" at each of the ~10 call sites across four item kinds —
    /// is what guarantees every one of them gets the same fail-fast behavior; hand-copying it previously
    /// let the relationship block silently miss the failure-mode check entirely (caught in review).
    /// </summary>
    private static void RecordFailureAndMaybeThrow(
        List<IngestionItemOutcome> outcomes,
        bool failFast,
        MemoryItemKind kind,
        IngestionStage stage,
        string errorCode,
        string? sourceKey,
        string? persistedId,
        Exception ex,
        string failFastMessage)
    {
        outcomes.Add(new IngestionItemOutcome
        {
            Kind = kind,
            Stage = stage,
            Status = IngestionItemStatus.Failed,
            SourceKey = sourceKey,
            PersistedId = persistedId,
            ErrorCode = errorCode,
            ErrorMessage = ex.Message,
            Retryable = true,
        });

        if (failFast)
            throw new MemoryIngestionException(failFastMessage, outcomes, ex);
    }
}
