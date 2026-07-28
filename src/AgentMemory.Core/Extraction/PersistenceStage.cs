using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Diagnostics;
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
    private readonly IMemoryPersistenceTransaction _persistenceTransaction;
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
        IMemoryPersistenceTransaction persistenceTransaction,
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
        _persistenceTransaction = persistenceTransaction ?? throw new ArgumentNullException(nameof(persistenceTransaction));
        _options = extractionOptions?.Value ?? new ExtractionOptions();
    }

    public async Task<PersistenceResult> PersistAsync(
        ExtractionStageResult extraction,
        string? ownerId = null,
        MemoryTrustLevel trustLevel = MemoryTrustLevel.Untrusted,
        CancellationToken cancellationToken = default)
    {
        // External embedding work is deliberately completed before the storage transaction opens.
        // Holding a database transaction while waiting on a model/provider would amplify contention
        // and make provider latency part of the database failure surface.
        using var activity = AgentMemoryDiagnostics.Source.StartActivity("memory.persist.total");
        if (activity is not null)
        {
            activity.SetTag("memory.persist.entities", extraction.ResolvedEntityMap.Count);
            activity.SetTag("memory.persist.facts", extraction.FilteredFacts.Count);
            activity.SetTag("memory.persist.preferences", extraction.FilteredPreferences.Count);
            activity.SetTag("memory.persist.relationships", extraction.FilteredRelationships.Count);
        }

        var prepared = await PrepareEmbeddingsAsync(extraction, cancellationToken).ConfigureAwait(false);
        if (_options.FailureMode != IngestionFailureMode.FailFast)
            return await PersistPreparedAsync(
                extraction, ownerId, trustLevel, prepared, cancellationToken).ConfigureAwait(false);

        return await _persistenceTransaction.ExecuteAsync(
            ct => PersistPreparedAsync(extraction, ownerId, trustLevel, prepared, ct),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<PersistenceResult> PersistPreparedAsync(
        ExtractionStageResult extraction,
        string? ownerId,
        MemoryTrustLevel trustLevel,
        PreparedEmbeddings prepared,
        CancellationToken cancellationToken)
    {
        var sourceMessageIds = extraction.SourceMessageIds;
        var failFast = _options.FailureMode == IngestionFailureMode.FailFast;
        var outcomes = new List<IngestionItemOutcome>(extraction.Outcomes);
        outcomes.AddRange(prepared.Outcomes);

        // 1. Embed + upsert entities; build a name→persisted Entity map for relationship resolution.
        var persistedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entity) in prepared.Entities)
        {
            // Trust is monotonic, never silently downgraded: when entity resolution (auto-merge/SAME_AS)
            // resolves this mention onto an EXISTING, previously-persisted entity, `entity` already carries
            // that entity's own prior Metadata/trust level. An unrelated later mention at a lower trust
            // level (e.g. an ordinary chat turn) must not erase a deliberately-elevated trust stamp (e.g.
            // from a curated ApplicationTrusted import) -- take whichever of the two is higher.
            var effectiveTrustLevel = MaxTrustLevel(entity.Metadata.GetTrustLevel(), trustLevel);
            var entityToSave = entity with { OwnerId = ownerId, Metadata = entity.Metadata.WithTrustLevel(effectiveTrustLevel) };

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
        foreach (var preparedFact in prepared.Facts)
        {
            var extracted = preparedFact.Item;
            var factEmbedding = preparedFact.Embedding;
            // factSourceKey (outcome/log identification) and the embedding below are both computed from the
            // freshly-extracted casing, even though the fact ultimately persisted may use an existing
            // record's casing instead when the #92 Phase 5 pre-fetch finds a case-insensitive match (see
            // below) -- a disclosed, cosmetic-only inconsistency (found in a post-Phase-5 holistic audit):
            // an outcome/log entry for a casing-only re-extraction won't textually match what was persisted,
            // and the surviving node's Embedding and Subject/Predicate/Object can reflect two different
            // casings of the same triple. Embeddings are semantically robust to case, so this hasn't been
            // observed to affect retrieval quality; not fixed here to keep this phase's blast radius narrow.
            var factSourceKey = $"{extracted.Subject} {extracted.Predicate} {extracted.Object}";

            try
            {
                // Trust is monotonic for owner-scoped facts too (#92 Phase 5), mirroring entities (Phase 3):
                // the repository's Upsert MERGEs on the exact {subject,predicate,object,owner} triple and its
                // Cypher ON MATCH unconditionally overwrites metadata, so re-extracting the identical triple
                // at a lower trust level (e.g. an ordinary chat turn re-stating a fact originally imported at
                // ApplicationTrusted) would otherwise silently erase the earlier elevation. Unlike entities,
                // facts have no upstream resolution step that hands PersistenceStage the prior record for
                // free, so this pre-fetch is the "one extra round-trip" the Phase 3 doc flagged as needed.
                // A lookup failure falls through to the same catch below as an ordinary persistence failure.
                //
                // Disclosed, unaddressed limitation (found in a post-Phase-5 holistic audit): this pre-fetch
                // and the Upsert below are two separate, non-atomic Neo4j round-trips, not one atomic
                // read-modify-write. Two concurrent extractions racing on the identical triple (e.g. a
                // curated ApplicationTrusted import racing an ordinary chat-turn extraction) could each read
                // the same stale prior state and independently compute their own "effective" trust, so
                // whichever Upsert commits last wins outright rather than the two being reconciled -- a
                // narrow, real gap in "never decreases" under genuine concurrency on the same triple.
                // Matches this codebase's existing precedent of disclosing rather than solving multi-step,
                // non-atomic writes (see the threat model's TT-12, record+provenance-edge non-atomicity).
                //
                // Only performed when ownerId is set (string.IsNullOrEmpty, matching how the rest of this
                // codebase treats an empty owner id the same as a null one -- e.g. DefaultMemoryIsolationPolicy):
                // FindByTripleAsync's MemoryScope? parameter follows the read/recall convention where an
                // unscoped lookup (null, or a scope with no OwnerId) means "search across every owner" -- the
                // opposite of what a null ownerId means on the WRITE side (the shared/global bucket). Passing
                // an owner-less scope here would risk adopting another owner's trust level into a shared fact
                // -- a cross-tenant leak. Unlike FindDuplicateAsync (whose raw ownerId parameter is documented
                // as "null -> shared bucket only"), there is no existing repository primitive for a safe
                // shared-bucket-only lookup, so shared/global facts don't get this protection yet -- a
                // disclosed, narrower-than-ideal limitation for this phase.
                //
                // includeShared: false -- deliberately excludes shared/global facts from the pre-fetch even
                // though MemoryScope.For defaults to including them. The default is right for READS (surface
                // everything the caller may see), but wrong here: with no ORDER BY, a shared fact and this
                // owner's own fact could both match the same triple, and picking up the shared one would
                // graft an unrelated record's ENTIRE metadata (not just its trust level) onto this owner's
                // fact -- conflating two conceptually distinct records that merely share text.
                //
                // FindByTripleAsync matches case-insensitively but Upsert's MERGE key is an exact-string
                // match -- if a match is found, this fact is built from the EXISTING record's Subject/
                // Predicate/Object (not the freshly-extracted casing) so the subsequent Upsert's MERGE
                // still targets the SAME node instead of creating a same-triple, different-casing duplicate.
                Fact? existingFact = string.IsNullOrEmpty(ownerId)
                    ? null
                    : await _factRepository.FindByTripleAsync(
                        extracted.Subject, extracted.Predicate, extracted.Object,
                        MemoryScope.For(ownerId, includeShared: false), cancellationToken).ConfigureAwait(false);
                var effectiveFactTrustLevel = existingFact is null
                    ? trustLevel
                    : MaxTrustLevel(existingFact.Metadata.GetTrustLevel(), trustLevel);
                var factMetadata = existingFact is null
                    ? MemoryTrustMetadataExtensions.CreateWithTrustLevel(effectiveFactTrustLevel)
                    : existingFact.Metadata.WithTrustLevel(effectiveFactTrustLevel);

                var fact = new Fact
                {
                    FactId = _idGenerator.GenerateId(),
                    Subject = existingFact?.Subject ?? extracted.Subject,
                    Predicate = existingFact?.Predicate ?? extracted.Predicate,
                    Object = existingFact?.Object ?? extracted.Object,
                    Confidence = extracted.Confidence,
                    ValidFrom = extracted.ValidFrom,
                    ValidUntil = extracted.ValidUntil,
                    Embedding = factEmbedding,
                    OwnerId = ownerId,
                    SourceMessageIds = sourceMessageIds,
                    CreatedAtUtc = _clock.UtcNow,
                    Metadata = factMetadata
                };

                // Facts MERGE on the natural {subject,predicate,object,owner_key} triple, and ON MATCH
                // deliberately never rewrites the surviving node's id (Neo4jFactRepository's own contract) --
                // so on a re-extraction hit, fact.FactId (the freshly-generated guid above) is orphaned and
                // was never actually persisted. Reassign from the repository's return value, mirroring the
                // entity block above, so RecordSuccess and the EXTRACTED_FROM loop below use the real,
                // surviving node's id.
                fact = await _factRepository.UpsertAsync(fact, cancellationToken).ConfigureAwait(false);
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
        foreach (var preparedPreference in prepared.Preferences)
        {
            var extracted = preparedPreference.Item;
            var prefEmbedding = preparedPreference.Embedding;
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

    private async Task<PreparedEmbeddings> PrepareEmbeddingsAsync(
        ExtractionStageResult extraction,
        CancellationToken cancellationToken)
    {
        var failFast = _options.FailureMode == IngestionFailureMode.FailFast;
        var outcomes = new List<IngestionItemOutcome>();
        var entities = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        var facts = new List<PreparedFact>(extraction.FilteredFacts.Count);
        var preferences = new List<PreparedPreference>(extraction.FilteredPreferences.Count);

        foreach (var (name, entity) in extraction.ResolvedEntityMap)
        {
            if (entity.Embedding is not null)
            {
                entities[name] = entity;
                continue;
            }

            try
            {
                var embedding = await _embeddingOrchestrator.EmbedEntityAsync(
                    entity.Name, cancellationToken).ConfigureAwait(false);
                entities[name] = entity with { Embedding = embedding };
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embedding for entity '{Name}'.", name);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Entity, IngestionStage.Embedding,
                    MemoryErrorCodes.EmbeddingGenerationFailed, name, null, ex,
                    $"Ingestion failed fast: embedding generation failed for entity '{name}'.");
            }
        }

        foreach (var extracted in extraction.FilteredFacts)
        {
            var sourceKey = $"{extracted.Subject} {extracted.Predicate} {extracted.Object}";
            try
            {
                var embedding = await _embeddingOrchestrator.EmbedFactAsync(
                    extracted.Subject, extracted.Predicate, extracted.Object, cancellationToken).ConfigureAwait(false);
                facts.Add(new PreparedFact(extracted, embedding));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embedding for fact '{Key}'.", sourceKey);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Fact, IngestionStage.Embedding,
                    MemoryErrorCodes.EmbeddingGenerationFailed, sourceKey, null, ex,
                    $"Ingestion failed fast: embedding generation failed for fact '{sourceKey}'.");
            }
        }

        foreach (var extracted in extraction.FilteredPreferences)
        {
            try
            {
                var embedding = await _embeddingOrchestrator.EmbedPreferenceAsync(
                    extracted.PreferenceText, cancellationToken).ConfigureAwait(false);
                preferences.Add(new PreparedPreference(extracted, embedding));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error generating embedding for preference '{Text}'.", extracted.PreferenceText);
                RecordFailureAndMaybeThrow(outcomes, failFast, MemoryItemKind.Preference, IngestionStage.Embedding,
                    MemoryErrorCodes.EmbeddingGenerationFailed, extracted.PreferenceText, null, ex,
                    "Ingestion failed fast: embedding generation failed for a preference.");
            }
        }

        return new PreparedEmbeddings(entities, facts, preferences, outcomes);
    }

    private sealed record PreparedEmbeddings(
        IReadOnlyDictionary<string, Entity> Entities,
        IReadOnlyList<PreparedFact> Facts,
        IReadOnlyList<PreparedPreference> Preferences,
        IReadOnlyList<IngestionItemOutcome> Outcomes);

    private sealed record PreparedFact(ExtractedFact Item, float[] Embedding);

    private sealed record PreparedPreference(ExtractedPreference Item, float[] Embedding);

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
