using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Services;

/// <summary>
/// Service for long-term (structured knowledge) memory operations.
/// </summary>
public sealed class LongTermMemoryService : ILongTermMemoryService
{
    private readonly IEntityRepository _entityRepo;
    private readonly IFactRepository _factRepo;
    private readonly IPreferenceRepository _prefRepo;
    private readonly IRelationshipRepository _relRepo;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly LongTermMemoryOptions _options;
    private readonly ILogger<LongTermMemoryService> _logger;

    public LongTermMemoryService(
        IEntityRepository entityRepo,
        IFactRepository factRepo,
        IPreferenceRepository prefRepo,
        IRelationshipRepository relRepo,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IOptions<LongTermMemoryOptions> options,
        ILogger<LongTermMemoryService> logger)
    {
        _entityRepo = entityRepo;
        _factRepo = factRepo;
        _prefRepo = prefRepo;
        _relRepo = relRepo;
        _embeddingOrchestrator = embeddingOrchestrator;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<Entity> AddEntityAsync(
        Entity entity,
        CancellationToken cancellationToken = default)
    {
        var finalEntity = entity;
        if (_options.GenerateEntityEmbeddings && entity.Embedding is null)
        {
            var text = string.IsNullOrEmpty(entity.Description) ? entity.Name : $"{entity.Name}: {entity.Description}";
            _logger.LogDebug("Generating embedding for entity {EntityId}", entity.EntityId);
            var embedding = await _embeddingOrchestrator.EmbedTextAsync(text, cancellationToken);
            finalEntity = entity with { Embedding = embedding };
        }
        return await _entityRepo.UpsertAsync(finalEntity, cancellationToken);
    }

    public Task<IReadOnlyList<Entity>> GetEntitiesByNameAsync(
        string name,
        bool includeAliases = true,
        CancellationToken cancellationToken = default)
    {
        return _entityRepo.GetByNameAsync(name, includeAliases, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> SearchEntitiesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _entityRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, cancellationToken);
        return scored.Select(r => r.Entity).ToList();
    }

    public async Task<Preference> AddPreferenceAsync(
        Preference preference,
        CancellationToken cancellationToken = default)
    {
        var finalPreference = preference;
        if (_options.GeneratePreferenceEmbeddings && preference.Embedding is null)
        {
            _logger.LogDebug("Generating embedding for preference {PreferenceId}", preference.PreferenceId);
            var embedding = await _embeddingOrchestrator.EmbedPreferenceAsync(preference.PreferenceText, cancellationToken);
            finalPreference = preference with { Embedding = embedding };
        }
        return await _prefRepo.UpsertAsync(finalPreference, cancellationToken);
    }

    public Task<IReadOnlyList<Preference>> GetPreferencesByCategoryAsync(
        string category,
        CancellationToken cancellationToken = default)
    {
        return _prefRepo.GetByCategoryAsync(category, cancellationToken);
    }

    public async Task<IReadOnlyList<Preference>> SearchPreferencesAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _prefRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, cancellationToken);
        return scored.Select(r => r.Preference).ToList();
    }

    public async Task<Fact> AddFactAsync(
        Fact fact,
        CancellationToken cancellationToken = default)
    {
        var finalFact = fact;
        if (_options.GenerateFactEmbeddings && fact.Embedding is null)
        {
            _logger.LogDebug("Generating embedding for fact {FactId}", fact.FactId);
            var embedding = await _embeddingOrchestrator.EmbedFactAsync(fact.Subject, fact.Predicate, fact.Object, cancellationToken);
            finalFact = fact with { Embedding = embedding };
        }
        return await _factRepo.UpsertAsync(finalFact, cancellationToken);
    }

    public Task<IReadOnlyList<Fact>> GetFactsBySubjectAsync(
        string subject,
        CancellationToken cancellationToken = default)
    {
        return _factRepo.GetBySubjectAsync(subject, cancellationToken);
    }

    public async Task<IReadOnlyList<Fact>> SearchFactsAsync(
        float[] queryEmbedding,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _factRepo.SearchByVectorAsync(queryEmbedding, limit, minScore, cancellationToken);
        return scored.Select(r => r.Fact).ToList();
    }

    public Task<Relationship> AddRelationshipAsync(
        Relationship relationship,
        CancellationToken cancellationToken = default)
    {
        return _relRepo.UpsertAsync(relationship, cancellationToken);
    }

    public Task<IReadOnlyList<Relationship>> GetEntityRelationshipsAsync(
        string entityId,
        CancellationToken cancellationToken = default)
    {
        return _relRepo.GetByEntityAsync(entityId, cancellationToken);
    }

    public Task DeletePreferenceAsync(
        string preferenceId,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Deleting preference {PreferenceId}", preferenceId);
        return _prefRepo.DeleteAsync(preferenceId, cancellationToken);
    }

    public async Task<IReadOnlyList<Entity>> SearchEntitiesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _entityRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, cancellationToken);
        return scored.Select(r => r.Entity).ToList();
    }

    public async Task<IReadOnlyList<Fact>> SearchFactsAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _factRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, cancellationToken);
        return scored.Select(r => r.Fact).ToList();
    }

    public async Task<IReadOnlyList<Preference>> SearchPreferencesAsOfAsync(
        float[] queryEmbedding,
        DateTimeOffset asOf,
        int limit = 10,
        double minScore = 0.0,
        CancellationToken cancellationToken = default)
    {
        var scored = await _prefRepo.SearchByVectorAsOfAsync(queryEmbedding, asOf, limit, minScore, cancellationToken);
        return scored.Select(r => r.Preference).ToList();
    }
}
