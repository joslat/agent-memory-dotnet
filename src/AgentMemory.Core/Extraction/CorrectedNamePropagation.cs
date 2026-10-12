using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Core.Extraction;

/// <summary>Propagates corrected names after the core persistence transaction and before rebuilding context.</summary>
internal sealed class CorrectedNamePropagation
{
    private readonly IEntityRepository _entityRepository;
    private readonly IFactRepository _factRepository;
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly ExtractionOptions _options;
    private readonly ILogger _logger;
    private readonly ClosingPipeline _closings;

    public CorrectedNamePropagation(IEntityRepository entityRepository, IFactRepository factRepository,
        IEmbeddingOrchestrator embeddingOrchestrator, IClock clock, IIdGenerator idGenerator,
        ExtractionOptions options, ILogger logger, ClosingPipeline closings)
    {
        _entityRepository = entityRepository;
        _factRepository = factRepository;
        _embeddingOrchestrator = embeddingOrchestrator;
        _clock = clock;
        _idGenerator = idGenerator;
        _options = options;
        _logger = logger;
        _closings = closings;
    }

    /// <summary>
    /// J-11 (<see cref="ExtractionOptions.RenameOnCorrectedName"/>). The renames this persist made: for each naming fact
    /// it wrote ("user | is named | Priya"), the naming facts of the same subject it closed ("… | Pruya"), found in the
    /// store rather than threaded through the write, so it holds for every write path (atomic, best-effort, replayed).
    /// Best-effort, like supersession: a failed rename leaves the two names side by side, never a failed ingestion.
    /// </summary>
    public async Task ApplyAsync(
        ExtractionStageResult extraction, string? ownerId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        if (!_options.RenameOnCorrectedName || string.IsNullOrWhiteSpace(ownerId)) return;
        var naming = extraction.FilteredFacts
            .Where(f => MemoryUserNames.IsNamingPredicate(f.Predicate) && !string.IsNullOrWhiteSpace(f.Object))
            .ToList();
        if (naming.Count == 0) return;

        var scope = MemoryScope.For(ownerId, includeShared: false);
        // The store's clock stamps invalidated_at; a second of slack keeps a close made by this persist inside.
        var from = since - TimeSpan.FromSeconds(1);
        foreach (var named in naming)
        {
            var name = named.Object.Trim();
            try
            {
                var replaced = (await _factRepository.GetBySubjectAsync(named.Subject, scope, cancellationToken).ConfigureAwait(false))
                    .Where(f => MemoryUserNames.IsNamingPredicate(f.Predicate) && f.InvalidatedAtUtc >= from &&
                                !string.Equals(f.Object.Trim(), name, StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Object.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                foreach (var old in replaced)
                    await RenameAsync(old, name, scope, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Renaming to '{Name}' failed; the old name stays beside it.", name);
            }
        }
    }

    /// <summary>J-11: the entity called <paramref name="old"/> becomes <paramref name="name"/>, and so do the facts about it.</summary>
    private async Task RenameAsync(string old, string name, MemoryScope scope, CancellationToken cancellationToken)
    {
        // The entity: only one that bears the old name as its own (an alias match is already the renamed one).
        var source = await _entityRepository.FindLiveByNameAsync(old, null, scope, cancellationToken).ConfigureAwait(false);
        if (source is not null && string.Equals(source.Name, old, StringComparison.OrdinalIgnoreCase))
        {
            var target = await _entityRepository.FindLiveByNameAsync(name, null, scope, cancellationToken).ConfigureAwait(false)
                ?? await _entityRepository.UpsertAsync(new Entity
                {
                    EntityId = _idGenerator.GenerateId(),
                    Name = name,
                    Type = source.Type,
                    Subtype = source.Subtype,
                    Confidence = source.Confidence,
                    OwnerId = scope.OwnerId,
                    CreatedAtUtc = _clock.UtcNow,
                    SourceMessageIds = source.SourceMessageIds,
                    Embedding = await _embeddingOrchestrator.EmbedAsync(name, cancellationToken).ConfigureAwait(false),
                }, cancellationToken).ConfigureAwait(false);
            if (target.EntityId == source.EntityId)
            {
                // Entity resolution already filed the new name under the old entity (as an alias): it is the same one,
                // so it is renamed in place, the new name its own and the old one kept as an alias.
                await _entityRepository.UpsertAsync(source with
                {
                    Name = name,
                    CanonicalName = name,
                    Aliases = [.. source.Aliases.Where(a => !string.Equals(a, name, StringComparison.OrdinalIgnoreCase))
                                   .Append(old).Distinct(StringComparer.OrdinalIgnoreCase)],
                    Embedding = await _embeddingOrchestrator.EmbedAsync(name, cancellationToken).ConfigureAwait(false),
                }, cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Renamed entity '{Old}' to '{Name}' in place ({Id}).", old, name, source.EntityId);
            }
            else
            {
                // Relationships and mentions move, the old name becomes an alias; then the old entity is closed.
                await _entityRepository.MergeEntitiesAsync(source.EntityId, target.EntityId, scope, cancellationToken).ConfigureAwait(false);
                await _closings.ApplyAsync(ClosingProposal.RetireEntity(source.EntityId,
                    scope, ClosingSource.CorrectedName), cancellationToken).ConfigureAwait(false);
                // The merge clears the survivor's embedding (its aliases changed), and nothing on this path re-embeds it:
                // the renamed person or pet dropped out of entity recall (found by the core.names validation pack). It is
                // embedded again from its name, as extraction embeds an entity and as the in-place branch above does.
                await _entityRepository.UpdateEmbeddingAsync(
                    target.EntityId, await _embeddingOrchestrator.EmbedAsync(target.Name, cancellationToken).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
                _logger.LogDebug("Renamed entity '{Old}' to '{Name}' ({Source} into {Target}).", old, name, source.EntityId, target.EntityId);
            }
        }

        // The facts said about the old name, restated under the new one, and (K-4, 38.2) the facts that name it as their
        // object ("Priya | owns | Rex"). A naming fact of another subject names a different thing and is left alone.
        var about = await _factRepository.GetBySubjectAsync(old, scope, cancellationToken).ConfigureAwait(false);
        var naming = await _factRepository.GetByObjectAsync(old, scope, cancellationToken).ConfigureAwait(false);
        var facts = about
            .Concat(naming.Where(f => !MemoryUserNames.IsNamingPredicate(f.Predicate)))
            .Where(f => f.InvalidatedAtUtc is null)
            .DistinctBy(f => f.FactId, StringComparer.Ordinal);
        string Renamed(string text) => string.Equals(text, old, StringComparison.Ordinal) ? name : text;
        foreach (var fact in facts)
        {
            var (subject, @object) = (Renamed(fact.Subject), Renamed(fact.Object));
            var metadata = new Dictionary<string, object>(fact.Metadata) { ["renamed_from"] = old };
            var restated = await _factRepository.UpsertAsync(fact with
            {
                FactId = _idGenerator.GenerateId(),
                Subject = subject,
                Object = @object,
                CreatedAtUtc = _clock.UtcNow,
                Metadata = metadata,
                Embedding = await _embeddingOrchestrator.EmbedFactAsync(subject, fact.Predicate, @object, cancellationToken)
                    .ConfigureAwait(false),
            }, cancellationToken).ConfigureAwait(false);
            if (restated.FactId == fact.FactId) continue;
            // Period-aware: the original is closed, never edited. The same fact under the right name corrects it, so
            // with BitemporalChanges its valid time stays as it was.
            await _closings.ApplyAsync(ClosingProposal.Fact(fact.FactId, restated.FactId,
                FactClosureReason.Correction, null, scope, ClosingSource.CorrectedName), cancellationToken)
                .ConfigureAwait(false);
            if (_options.LinkFactsToEntities)
                await LinkRestatedAsync(restated, scope, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// K-4. A restated fact is linked, as extraction links a new one, to the live entities its subject and object name (the
    /// original's links stay on the closed original). An enrichment: a failure is logged, never the rename's failure.
    /// </summary>
    private async Task LinkRestatedAsync(Fact restated, MemoryScope scope, CancellationToken cancellationToken)
    {
        foreach (var named in new[] { restated.Subject, restated.Object }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (await _entityRepository.FindLiveByNameAsync(named, null, scope, cancellationToken).ConfigureAwait(false) is { } entity)
                    await _factRepository.CreateAboutRelationshipAsync(restated.FactId, entity.EntityId, cancellationToken)
                        .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Linking restated fact '{Id}' to '{Name}' failed.", restated.FactId, named);
            }
        }
    }
}
