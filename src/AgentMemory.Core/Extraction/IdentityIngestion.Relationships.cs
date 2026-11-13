using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Resolution;

namespace AgentMemory.Core.Extraction;

internal sealed partial class IdentityIngestion
{
    internal IReadOnlyList<IdentityRelationshipObservation> PrepareRelationshipObservations(
        IReadOnlyList<ExtractedRelationship> relationships, IReadOnlyList<string> sourceMessageIds, string? ownerId)
    {
        if (relationships.Count == 0) return [];
        if (!Enabled || _repository?.SupportsRelationshipIngestion != true)
            throw new NotSupportedException("The enabled identity backend cannot preserve original relationship observations.");
        if (relationships.Count > IdentityRelationshipPayload.MaxObservations)
            throw new ArgumentException("Identity ingestion exceeds its relationship-observation bound.");
        var observations = new List<IdentityRelationshipObservation>(relationships.Count);
        foreach (var relationship in relationships)
        {
            var frozen = IdentityRelationshipPayload.Capture(new()
            {
                ObservationId = "preflight", OwnerId = ownerId, Observation = relationship,
                SourceMessageIds = sourceMessageIds, CreatedAtUtc = _clock.GetUtcNow()
            }, ownerId);
            observations.Add(frozen with { ObservationId = "identity-relationship-observation-" +
                IdentityRelationshipPayload.Fingerprint(frozen).ToLowerInvariant() });
        }
        return observations;
    }

    internal static IReadOnlyList<IdentityRelationshipObservation> WithRelationshipEndpoints(
        IReadOnlyList<IdentityRelationshipObservation> observations, IReadOnlyList<IdentityDecisionWrite> plans)
    {
        if (observations.Count == 0) return observations;
        var decisions = plans.DistinctBy(plan => plan.Decision.ObservationKey)
            .GroupBy(plan => MemoryTripleCanonicalizer.CanonicalValue(plan.Decision.Observation.Name), StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().Decision.DecisionId, StringComparer.Ordinal);
        return observations.Select(observation => observation with
        {
            SourceDecisionId = decisions.GetValueOrDefault(MemoryTripleCanonicalizer.CanonicalValue(observation.Observation.SourceEntity)),
            TargetDecisionId = decisions.GetValueOrDefault(MemoryTripleCanonicalizer.CanonicalValue(observation.Observation.TargetEntity))
        }).ToArray();
    }

    internal async Task<RelationshipPersistenceResult> PersistRelationshipObservationsAsync(
        IReadOnlyList<IdentityRelationshipObservation> observations, IReadOnlyDictionary<string, Entity> entities,
        string? ownerId, bool includeShared, List<IngestionItemOutcome> outcomes, CancellationToken cancellationToken)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var inputs = new List<(Relationship Item, string SourceKey)>();
        foreach (var observation in observations.DistinctBy(value => value.ObservationId))
        {
            var saved = await _repository!.SaveRelationshipObservationAsync(observation, ownerId,
                includeShared, cancellationToken).ConfigureAwait(false);
            ids.Add(saved.ObservationId);
            var original = saved.Observation;
            var key = $"{original.SourceEntity}-{original.RelationshipType}->{original.TargetEntity}";
            PersistenceOutcomes.RecordSuccess(outcomes, MemoryItemKind.Relationship, key,
                saved.ObservationId, MemoryWriteEffect.Unreported);
            // Only a currently grounded projection can participate in writer-named closings.
            // An unresolved claim remains stored without invented entity IDs or closing authority.
            if (saved.SourceDecisionId is not null && saved.TargetDecisionId is not null &&
                saved.SourceDecisionId == observation.SourceDecisionId && saved.TargetDecisionId == observation.TargetDecisionId &&
                entities.TryGetValue(original.SourceEntity, out var source) &&
                entities.TryGetValue(original.TargetEntity, out var target) && source.EntityId != target.EntityId)
                inputs.Add((new()
                {
                    RelationshipId = saved.ObservationId, SourceEntityId = source.EntityId, TargetEntityId = target.EntityId,
                    RelationshipType = original.RelationshipType, Confidence = original.Confidence,
                    Description = original.Description, Attributes = original.Attributes, OwnerId = ownerId,
                    SourceMessageIds = saved.SourceMessageIds, CreatedAtUtc = saved.CreatedAtUtc, ValidUntil = saved.EndedAtUtc
                }, key));
        }
        return new() { Inputs = inputs, PersistedIds = ids, Count = ids.Count };
    }
}
