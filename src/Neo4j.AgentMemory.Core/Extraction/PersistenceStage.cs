using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction;

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
    private readonly ILogger<PersistenceStage> _logger;

    public PersistenceStage(
        IEmbeddingOrchestrator embeddingOrchestrator,
        IEntityRepository entityRepository,
        IFactRepository factRepository,
        IPreferenceRepository preferenceRepository,
        IRelationshipRepository relationshipRepository,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<PersistenceStage> logger)
    {
        _embeddingOrchestrator = embeddingOrchestrator;
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _preferenceRepository = preferenceRepository;
        _relationshipRepository = relationshipRepository;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    public async Task<PersistenceResult> PersistAsync(
        ExtractionStageResult extraction,
        CancellationToken cancellationToken = default)
    {
        var sourceMessageIds = extraction.SourceMessageIds;

        // 1. Embed + upsert entities; build a name→persisted Entity map for relationship resolution.
        var persistedEntityMap = new Dictionary<string, Entity>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, entity) in extraction.ResolvedEntityMap)
        {
            try
            {
                var entityToSave = entity;
                if (entityToSave.Embedding is null)
                {
                    var embedding = await _embeddingOrchestrator.EmbedEntityAsync(
                        entityToSave.Name, cancellationToken);
                    entityToSave = entityToSave with { Embedding = embedding };
                }

                entityToSave = await _entityRepository.UpsertAsync(entityToSave, cancellationToken);
                persistedEntityMap[name] = entityToSave;

                foreach (var msgId in sourceMessageIds)
                {
                    try
                    {
                        await _entityRepository.CreateExtractedFromRelationshipAsync(
                            entityToSave.EntityId, msgId, cancellationToken: cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Failed to create EXTRACTED_FROM for entity '{Id}' → message '{MsgId}'.",
                            entityToSave.EntityId, msgId);
                    }
                }

                _logger.LogDebug("Persisted entity '{Name}' (id={Id}).", entityToSave.Name, entityToSave.EntityId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error persisting entity '{Name}'.", name);
            }
        }

        // 2. Embed + upsert facts.
        var persistedFactCount = 0;
        foreach (var extracted in extraction.FilteredFacts)
        {
            try
            {
                var factEmbedding = await _embeddingOrchestrator.EmbedFactAsync(
                    extracted.Subject, extracted.Predicate, extracted.Object, cancellationToken);

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
                _logger.LogDebug("Persisted fact '{S} {P} {O}'.", fact.Subject, fact.Predicate, fact.Object);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error persisting fact '{S} {P} {O}'.",
                    extracted.Subject, extracted.Predicate, extracted.Object);
            }
        }

        // 3. Embed + upsert preferences.
        var persistedPrefCount = 0;
        foreach (var extracted in extraction.FilteredPreferences)
        {
            try
            {
                var prefEmbedding = await _embeddingOrchestrator.EmbedPreferenceAsync(
                    extracted.PreferenceText, cancellationToken);

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
                _logger.LogError(ex, "Error persisting preference '{Text}'.", extracted.PreferenceText);
            }
        }

        // 4. Persist relationships — resolve entity IDs from the upserted entity map.
        var persistedRelCount = 0;
        foreach (var extracted in extraction.FilteredRelationships)
        {
            if (!persistedEntityMap.TryGetValue(extracted.SourceEntity, out var sourceEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — source entity '{Source}' was not persisted.",
                    extracted.SourceEntity);
                continue;
            }

            if (!persistedEntityMap.TryGetValue(extracted.TargetEntity, out var targetEntity))
            {
                _logger.LogWarning(
                    "Skipping relationship — target entity '{Target}' was not persisted.",
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
                    "Persisted relationship '{Src}-{Type}->{Tgt}'.",
                    extracted.SourceEntity, extracted.RelationshipType, extracted.TargetEntity);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Error persisting relationship '{Src}->{Tgt}'.",
                    extracted.SourceEntity, extracted.TargetEntity);
            }
        }

        return new PersistenceResult
        {
            EntityCount = persistedEntityMap.Count,
            FactCount = persistedFactCount,
            PreferenceCount = persistedPrefCount,
            RelationshipCount = persistedRelCount
        };
    }
}
