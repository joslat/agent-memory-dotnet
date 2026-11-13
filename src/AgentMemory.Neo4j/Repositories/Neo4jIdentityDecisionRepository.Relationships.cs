using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Resolution;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jIdentityDecisionRepository
{
    public bool SupportsRelationshipIngestion => IsEnabled;

    public async Task<IdentityRelationshipObservation?> GetRelationshipObservationAsync(string observationId,
        string? ownerId = null, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken); Text(observationId);
        if (ownerId is not null) Text(ownerId);
        return await tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(IdentityRelationshipQueries.GetObservation,
                new { id = observationId, ownerScope = Scope(ownerId) }).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return rows.Count == 0 ? null : DecodeRelationship(rows.Single());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdentityRelationshipObservation> SaveRelationshipObservationAsync(IdentityRelationshipObservation observation,
        string? ownerId = null, bool includeShared = false, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        var frozen = IdentityRelationshipPayload.Capture(observation, ownerId);
        var reuse = IdentityWriteScope.Reuse(tx, Scopes(ownerId, includeShared));
        return await tx.WriteAsync(async runner =>
        {
            if (!reuse)
                foreach (var scope in Scopes(ownerId, includeShared).Order(StringComparer.Ordinal))
                {
                    var locked = await runner.RunAsync(IdentityDecisionQueries.LockScope, new { ownerScope = scope }).ConfigureAwait(false);
                    await locked.ConsumeAsync().ConfigureAwait(false);
                }
            await ValidateRoots(runner, frozen.SourceMessageIds, ownerId, includeShared, cancellationToken).ConfigureAwait(false);
            var collision = await runner.RunAsync(IdentityRelationshipQueries.LegacyIdCollision,
                new { id = frozen.ObservationId }).ConfigureAwait(false);
            if ((await collision.SingleAsync().ConfigureAwait(false))["collision"].As<bool>())
                throw new InvalidOperationException("The observation ID collides with an ordinary relationship; no legacy edge is adopted.");
            var replayCursor = await runner.RunAsync(IdentityRelationshipQueries.ReadReplay,
                new { id = frozen.ObservationId, ownerScope = Scope(ownerId) }).ConfigureAwait(false);
            var replay = await replayCursor.ToListAsync().ConfigureAwait(false);
            if (replay.Count > 1 || replay.Any(row => !row["authorized"].As<bool>()))
                throw new InvalidOperationException("The relationship observation ID belongs to another owner or is duplicated.");
            var current = replay.Count == 0 ? frozen : DecodeRelationship(replay.Single());
            if (IdentityRelationshipPayload.Fingerprint(current) != IdentityRelationshipPayload.Fingerprint(frozen))
                throw new InvalidOperationException("A relationship replay cannot replace the original assertion or provenance.");

            var ids = new[] { current.SourceDecisionId, current.TargetDecisionId }.OfType<string>()
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var decisions = new Dictionary<string, IdentityDecision>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var cursor = await runner.RunAsync(IdentityRelationshipQueries.ReadEndpointDecision,
                    new { decisionId = id, ownerScope = Scope(ownerId) }).ConfigureAwait(false);
                var rows = await cursor.ToListAsync().ConfigureAwait(false);
                if (rows.Count != 1) throw new InvalidOperationException("A relationship endpoint decision is not in the exact owner scope.");
                var decision = IdentityDecisionPayload.Decode(rows[0]["recordJson"].As<string>());
                if (decision.Observation.OwnerId != ownerId || !Roots(decision).Intersect(current.SourceMessageIds, StringComparer.Ordinal).Any())
                    throw new InvalidOperationException("Relationship endpoint support must share original source provenance.");
                if (decision.Status == IdentityDecisionKind.Confirmed)
                {
                    var target = await ReadCurrentTarget(runner, decision, includeShared, cancellationToken).ConfigureAwait(false);
                    if (target is null || Snapshot(target, decision.Policy) != rows[0]["targetSnapshot"].As<string>())
                        throw new InvalidOperationException("The relationship endpoint interpretation needs explicit re-evaluation.");
                }
                decisions.Add(id, decision);
                if (replay.Count == 0)
                {
                    var bound = await runner.RunAsync(IdentityRelationshipQueries.BoundIds,
                        new { decisionId = id, ownerScope = Scope(ownerId), limit = IdentityRelationshipPayload.MaxObservations + 1 }).ConfigureAwait(false);
                    if ((await bound.ToListAsync().ConfigureAwait(false)).Count >= IdentityRelationshipPayload.MaxObservations)
                        throw new InvalidOperationException("The decision exceeds its relationship-observation support bound.");
                }
                cancellationToken.ThrowIfCancellationRequested();
            }
            ValidateRelationshipSurface(current.Observation.SourceEntity, current.SourceDecisionId, decisions);
            ValidateRelationshipSurface(current.Observation.TargetEntity, current.TargetDecisionId, decisions);
            if (replay.Count == 0)
            {
                var created = await runner.RunAsync(IdentityRelationshipQueries.CreateObservation, new
                {
                    id = current.ObservationId, ownerScope = Scope(ownerId), ownerId,
                    recordJson = IdentityRelationshipPayload.Encode(current), sourceIds = current.SourceMessageIds,
                    relationType = current.Observation.RelationshipType, confidence = current.Observation.Confidence,
                    description = current.Observation.Description, attributes = Neo4jRecordMapper.SerializeMetadata(current.Observation.Attributes),
                    sourceDecisionId = current.SourceDecisionId, targetDecisionId = current.TargetDecisionId,
                    createdAt = current.CreatedAtUtc.ToString("O"), endedAt = current.EndedAtUtc?.ToString("O")
                }).ConfigureAwait(false);
                await created.ConsumeAsync().ConfigureAwait(false);
            }
            foreach (var (id, decision) in decisions)
            {
                var target = await runner.RunAsync(IdentityRelationshipQueries.EnsureDecisionTarget, new
                { decisionId = id, ownerScope = Scope(ownerId), ownerId, includeShared,
                    targetId = decision.ConfirmedEntityId ?? decision.Observation.EntityId }).ConfigureAwait(false);
                if ((await target.ToListAsync().ConfigureAwait(false)).Count != 1)
                    throw new InvalidOperationException("An endpoint target is missing, outside scope or disagrees with its decision.");
            }
            if (current.SourceDecisionId is { } source)
                await LinkRelationshipRole(runner, IdentityRelationshipQueries.LinkSource, current.ObservationId, source, ownerId, cancellationToken).ConfigureAwait(false);
            if (current.TargetDecisionId is { } targetId)
                await LinkRelationshipRole(runner, IdentityRelationshipQueries.LinkTarget, current.ObservationId, targetId, ownerId, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return current;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static IdentityRelationshipObservation DecodeRelationship(IRecord row)
    {
        var value = IdentityRelationshipPayload.Decode(row["recordJson"].As<string>());
        return value with { EndedAtUtc = Neo4jDateTimeHelper.ReadNullableDateTimeOffset(row["endedAt"]) };
    }

    private static void ValidateRelationshipSurface(string surface, string? id, IReadOnlyDictionary<string, IdentityDecision> decisions)
    {
        if (id is not null && MemoryTripleCanonicalizer.CanonicalValue(surface) !=
            MemoryTripleCanonicalizer.CanonicalValue(decisions[id].Observation.Name))
            throw new InvalidOperationException("A relationship role does not match the decision's original mention.");
    }

    private static async Task LinkRelationshipRole(IAsyncQueryRunner runner, string query, string id,
        string decisionId, string? ownerId, CancellationToken cancellationToken)
    {
        var cursor = await runner.RunAsync(query, new { id, decisionId, ownerScope = Scope(ownerId) }).ConfigureAwait(false);
        if ((await cursor.ToListAsync().ConfigureAwait(false)).Count != 1)
            throw new InvalidOperationException("Relationship endpoint support could not be linked in its owner scope.");
        cancellationToken.ThrowIfCancellationRequested();
    }
}
