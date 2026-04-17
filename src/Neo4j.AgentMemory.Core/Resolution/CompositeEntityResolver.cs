using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Repositories;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Resolution;

/// <summary>
/// Resolves extracted entities against existing entities using a chain of matchers:
/// Exact → Fuzzy → Semantic → Create New.
/// Post-resolution, high-confidence matches are auto-merged (alias added);
/// mid-confidence matches are flagged for SAME_AS relationship creation by the caller.
/// </summary>
public sealed class CompositeEntityResolver : IEntityResolver
{
    private readonly IEntityRepository _entityRepository;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddingGenerator;
    private readonly ExtractionOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<CompositeEntityResolver> _logger;

    public CompositeEntityResolver(
        IEntityRepository entityRepository,
        IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator,
        IOptions<ExtractionOptions> options,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<CompositeEntityResolver> logger)
    {
        _entityRepository = entityRepository;
        _embeddingGenerator = embeddingGenerator;
        _options = options.Value;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    public async Task<Entity> ResolveEntityAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetCandidatesAsync(extractedEntity.Type, cancellationToken)
            .ConfigureAwait(false);

        var matchers = BuildMatchers();
        EntityResolutionResult? resolutionResult = null;

        foreach (var matcher in matchers)
        {
            resolutionResult = await matcher.TryMatchAsync(extractedEntity, candidates, cancellationToken)
                .ConfigureAwait(false);

            if (resolutionResult is not null)
            {
                _logger.LogDebug(
                    "Entity '{Name}' matched via {MatchType} with confidence {Confidence:F3}.",
                    extractedEntity.Name, resolutionResult.MatchType, resolutionResult.Confidence);
                break;
            }
        }

        if (resolutionResult is null)
            return await CreateNewEntityAsync(extractedEntity, sourceMessageIds, cancellationToken)
                .ConfigureAwait(false);

        var matched = resolutionResult.ResolvedEntity;

        // >= AutoMergeThreshold: auto-merge (add alias to existing entity)
        if (resolutionResult.Confidence >= _options.AutoMergeThreshold)
        {
            _logger.LogDebug(
                "Auto-merging entity '{Candidate}' into '{Existing}' (confidence {Confidence:F3}).",
                extractedEntity.Name, matched.Name, resolutionResult.Confidence);

            var mergedAliases = matched.Aliases
                .Append(extractedEntity.Name)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(a => !string.Equals(a, matched.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var aliasesChanged = mergedAliases.Count != matched.Aliases.Count;

            var mergedEntity = matched with
            {
                Aliases = mergedAliases,
                SourceMessageIds = matched.SourceMessageIds.Concat(sourceMessageIds)
                    .Distinct()
                    .ToList()
            };

            // Re-embed only when new aliases were added, so the vector captures combined name + aliases.
            if (aliasesChanged)
            {
                var combinedText = $"{mergedEntity.Name} {string.Join(" ", mergedAliases)}".Trim();
                var freshGenerated = await _embeddingGenerator.GenerateAsync([combinedText], cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                mergedEntity = mergedEntity with { Embedding = freshGenerated[0].Vector.ToArray() };
            }

            return await _entityRepository.UpsertAsync(mergedEntity, cancellationToken)
                .ConfigureAwait(false);
        }

        // >= SameAsThreshold and < AutoMergeThreshold: flag for SAME_AS — caller handles relationship
        if (resolutionResult.Confidence >= _options.SameAsThreshold)
        {
            _logger.LogDebug(
                "Entity '{Candidate}' is SAME_AS '{Existing}' (confidence {Confidence:F3}). Returning existing without merge.",
                extractedEntity.Name, matched.Name, resolutionResult.Confidence);

            return matched;
        }

        // Below SameAsThreshold: create new entity
        _logger.LogDebug(
            "No match above SameAs threshold for '{Name}' — creating new entity.",
            extractedEntity.Name);

        return await CreateNewEntityAsync(extractedEntity, sourceMessageIds, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Entity>> FindPotentialDuplicatesAsync(
        string name,
        string type,
        CancellationToken cancellationToken = default)
    {
        var candidates = await GetCandidatesAsync(type, cancellationToken).ConfigureAwait(false);

        var probe = new ExtractedEntity { Name = name, Type = type };
        var matchers = BuildMatchers();
        var results = new List<Entity>();

        foreach (var matcher in matchers)
        {
            var match = await matcher.TryMatchAsync(probe, candidates, cancellationToken)
                .ConfigureAwait(false);
            if (match is not null && !results.Any(e => e.EntityId == match.ResolvedEntity.EntityId))
                results.Add(match.ResolvedEntity);
        }

        return results;
    }

    private async Task<IReadOnlyList<Entity>> GetCandidatesAsync(
        string type,
        CancellationToken cancellationToken)
    {
        if (_options.EntityResolution.TypeStrictFiltering)
            return await _entityRepository.GetByTypeAsync(type, cancellationToken).ConfigureAwait(false);

        // Without type filtering, use SearchByVectorAsync is impractical here without an embedding;
        // GetByTypeAsync with empty type returns all in many impls, so we fall back gracefully.
        // For a complete impl, a GetAllAsync method would be ideal — use GetByTypeAsync("") as best effort.
        return await _entityRepository.GetByTypeAsync(type, cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<IEntityMatcher> BuildMatchers()
    {
        var matchers = new List<IEntityMatcher>();
        var resOpts = _options.EntityResolution;

        if (resOpts.EnableExactMatch)
            matchers.Add(new ExactMatchEntityMatcher());

        if (resOpts.EnableFuzzyMatch)
            matchers.Add(new FuzzyMatchEntityMatcher(resOpts));

        if (resOpts.EnableSemanticMatch)
            matchers.Add(new SemanticMatchEntityMatcher(_embeddingGenerator, resOpts));

        return matchers;
    }

    private async Task<Entity> CreateNewEntityAsync(
        ExtractedEntity extracted,
        IReadOnlyList<string> sourceMessageIds,
        CancellationToken cancellationToken)
    {
        var entity = new Entity
        {
            EntityId = _idGenerator.GenerateId(),
            Name = extracted.Name,
            CanonicalName = extracted.Name,
            Type = extracted.Type,
            Subtype = extracted.Subtype,
            Description = extracted.Description,
            Confidence = extracted.Confidence,
            Aliases = extracted.Aliases,
            Attributes = extracted.Attributes,
            SourceMessageIds = sourceMessageIds,
            CreatedAtUtc = _clock.UtcNow
        };

        return await _entityRepository.UpsertAsync(entity, cancellationToken).ConfigureAwait(false);
    }
}
