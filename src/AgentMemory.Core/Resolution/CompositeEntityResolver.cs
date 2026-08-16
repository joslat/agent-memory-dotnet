using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// Resolves extracted entities against existing entities using a chain of matchers:
/// Exact → Fuzzy → Semantic → Create New.
/// Post-resolution, high-confidence matches are auto-merged (alias added);
/// mid-confidence matches are flagged for SAME_AS relationship creation by the caller.
/// </summary>
/// <remarks>
/// R1 scoping: when a <see cref="MemoryScope"/> with an owner is supplied, the candidate set is confined
/// to the owner's own + shared entities, so resolution can never reach another owner's <i>private</i>
/// entity. Note that with the default <c>IncludeShared=true</c>, a scoped owner's extraction <i>can</i>
/// still auto-merge into (and enrich the aliases/description of) a <b>shared</b> (owner_id IS NULL)
/// entity — this is intentional "shared knowledge grows collaboratively" behavior, not a cross-owner
/// leak (a future opt-in option could make shared knowledge read-only per owner if a deployment needs it).
/// </remarks>
internal sealed partial class CompositeEntityResolver : IEntityResolver, IExtractionEntityResolver
{
    private readonly IEntityRepository _entityRepository;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly ExtractionOptions _options;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ILogger<CompositeEntityResolver> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="CompositeEntityResolver"/> class.
    /// </summary>
    public CompositeEntityResolver(
        IEntityRepository entityRepository,
        IEmbeddingOrchestrator embeddingOrchestrator,
        IOptions<ExtractionOptions> options,
        IClock clock,
        IIdGenerator idGenerator,
        ILogger<CompositeEntityResolver> logger)
    {
        _entityRepository = entityRepository;
        _embeddingOrchestrator = embeddingOrchestrator;
        _options = options.Value;
        _clock = clock;
        _idGenerator = idGenerator;
        _logger = logger;
    }

    public Task<Entity> ResolveEntityAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        ResolveAndRememberAsync(
            extractedEntity, sourceMessageIds, scope, persistResolution: true, cancellationToken);

    public Task<Entity> ResolveForPersistenceAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default) =>
        ResolveAndRememberAsync(
            extractedEntity, sourceMessageIds, scope, persistResolution: false, cancellationToken);

    /// <summary>
    /// Resolves an entity either for a direct caller (preserving the historical persist-on-resolve
    /// behavior) or for fail-fast ExtractionStage, which must remain side-effect free until
    /// PersistenceStage opens the logical transaction.
    /// </summary>
    private async Task<Entity> ResolveEntityCoreAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope,
        bool persistResolution,
        CancellationToken cancellationToken)
    {
        var candidates = await GetCandidatesAsync(extractedEntity, scope, cancellationToken)
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
            return await CreateNewEntityAsync(extractedEntity, sourceMessageIds, scope, persistResolution, cancellationToken)
                .ConfigureAwait(false);

        var matched = resolutionResult.ResolvedEntity;

        // >= AutoMergeThreshold: auto-merge (add alias to existing entity) — ONLY when auto-merge is
        // enabled. With EnableAutoMerge=false a high-confidence match falls through to the SAME_AS band
        // below (non-destructive: the entities are linked, not folded), so a user who disabled auto-merge
        // to keep distinct-but-similar entities separate is honored instead of silently losing one.
        if (_options.EnableAutoMerge && resolutionResult.Confidence >= _options.AutoMergeThreshold)
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
                var freshEmbedding = await _embeddingOrchestrator.EmbedTextAsync(combinedText, cancellationToken)
                    .ConfigureAwait(false);
                mergedEntity = mergedEntity with { Embedding = freshEmbedding };
            }

            return persistResolution
                ? await _entityRepository.UpsertAsync(mergedEntity, cancellationToken).ConfigureAwait(false)
                : mergedEntity;
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

        return await CreateNewEntityAsync(extractedEntity, sourceMessageIds, scope, persistResolution, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<Entity>> FindPotentialDuplicatesAsync(
        string name,
        string type,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        // The probe is built first so duplicate-finding sees the same candidate set resolution does --
        // including the non-strict widening, which is if anything more wanted here: a cross-type
        // duplicate is exactly the kind this surface exists to surface.
        var probe = new ExtractedEntity { Name = name, Type = type };

        var candidates = await GetCandidatesAsync(probe, scope, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// The candidate set a match is chosen from: same-type entities, plus — when
    /// <see cref="EntityResolutionOptions.TypeStrictFiltering"/> is off — same-<i>name</i> entities of
    /// any type.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The flag used to do nothing.</b> Candidates were always fetched by type, so turning strict
    /// filtering off changed no behaviour and gave the caller no signal that it hadn't. The case it
    /// exists for is extractor mistyping: the same real-world entity extracted as <c>Organization</c>
    /// in one turn and <c>Location</c> in the next is, under strict filtering, permanently two entities.
    /// </para>
    /// <para>
    /// <b>Why by name rather than everything.</b> An earlier note here reasoned that non-strict mode was
    /// unimplementable because "the repository has no unfiltered GetAll contract" — true, but the wrong
    /// contract to want. Loading every entity in the owner's graph on each resolution would be an
    /// unbounded read on the write path. <see cref="IEntityRepository.GetByNameAsync"/> is bounded by
    /// the name, matches aliases, carries the owner filter, and covers the mistyping case exactly.
    /// </para>
    /// <para>
    /// The by-name read is deliberately <b>not</b> routed through the batch snapshot: that cache is
    /// keyed by type, and its pre-warm pass (<c>PrepareCandidatesAsync</c>) knows the types in a batch
    /// but not the names. Widening the key for a non-default path would slow the default one.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<Entity>> GetCandidatesAsync(
        ExtractedEntity extracted,
        MemoryScope? scope,
        CancellationToken cancellationToken)
    {
        var byType = await GetBatchCandidatesAsync(extracted.Type, scope, cancellationToken)
            .ConfigureAwait(false);

        if (_options.EntityResolution.TypeStrictFiltering || string.IsNullOrWhiteSpace(extracted.Name))
            return byType;

        // Same scope, always. Relaxing the TYPE boundary must never relax the OWNER one -- that would
        // turn a matching convenience into a cross-tenant leak on the write path.
        var byName = await _entityRepository
            .GetByNameAsync(extracted.Name, includeAliases: true, scope, cancellationToken)
            .ConfigureAwait(false);

        if (byName.Count == 0)
            return byType;

        // Same-type candidates first, so ordering-sensitive matchers see today's list before the
        // widened tail. Dedup by id: a same-type entity that also matches by name is one candidate.
        var seen = new HashSet<string>(byType.Select(e => e.EntityId), StringComparer.Ordinal);
        var combined = new List<Entity>(byType);
        foreach (var entity in byName)
        {
            if (seen.Add(entity.EntityId))
                combined.Add(entity);
        }

        _logger.LogDebug(
            "Type-strict filtering off: widened '{Name}' candidates from {Typed} to {Total}.",
            extracted.Name, byType.Count, combined.Count);

        return combined;
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
            matchers.Add(new SemanticMatchEntityMatcher(_embeddingOrchestrator, resOpts));

        return matchers;
    }

    private async Task<Entity> CreateNewEntityAsync(
        ExtractedEntity extracted,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope,
        bool persistResolution,
        CancellationToken cancellationToken)
    {
        var entity = new Entity
        {
            EntityId = _idGenerator.GenerateId(),
            // R1: stamp the owner from the resolution scope so the resolver's output is self-consistently
            // scoped (defense-in-depth; the persistence stage also stamps owner_id, but a direct caller
            // would otherwise create a private entity as owner_id=NULL/shared). Null scope ⇒ shared.
            OwnerId = scope?.OwnerId,
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

        return persistResolution
            ? await _entityRepository.UpsertAsync(entity, cancellationToken).ConfigureAwait(false)
            : entity;
    }
}
