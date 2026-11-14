using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Repositories;

/// <summary>Atomic, source-preserving decision storage. No model runs inside these transactions.</summary>
internal sealed partial class Neo4jIdentityDecisionRepository(INeo4jTransactionRunner tx, IOptions<Neo4jOptions> options, TimeProvider? clock = null)
    : IIdentityDecisionRepository
{
    public bool IsEnabled => options.Value.Extensions.Contains("identity-evidence");
    public bool SupportsAtomicIngestion => IsEnabled && tx is INeo4jAtomicTransactionRunner;
    public bool HasActiveWriteTransaction => tx is INeo4jAtomicTransactionRunner atomic && atomic.HasActiveWriteTransaction;

    public async Task<T> ExecuteScopedAsync<T>(string? ownerId, bool includeShared, IReadOnlyList<string> sourceMessageIds,
        Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken); ArgumentNullException.ThrowIfNull(work);
        if (HasActiveWriteTransaction) throw new NotSupportedException("Evidence-based ingestion must own its outer transaction boundary.");
        ArgumentNullException.ThrowIfNull(sourceMessageIds);
        if (sourceMessageIds.Count > 256) throw new ArgumentException("Identity source roots exceed their bound.");
        var sourceIds = sourceMessageIds.Distinct(StringComparer.Ordinal).ToArray();
        foreach (var source in sourceIds) Text(source);
        if (ownerId is not null) Text(ownerId);
        if (tx is not INeo4jAtomicTransactionRunner atomic)
            throw new NotSupportedException("The configured Neo4j runner cannot provide atomic identity ingestion.");
        var scopes = Scopes(ownerId, includeShared).Order(StringComparer.Ordinal).ToArray();
        // Establish infrastructure rows outside the logical ingestion. MERGE's index locks must not
        // be upgraded to mutation locks in the same transaction during a concurrent first write.
        await tx.WriteAsync(async runner =>
        {
            foreach (var scope in scopes)
            {
                var cursor = await runner.RunAsync(IdentityDecisionQueries.EnsureScope, new { ownerScope = scope }).ConfigureAwait(false);
                await cursor.ConsumeAsync().ConfigureAwait(false);
            }
            return true;
        }, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; ; attempt++)
        {
            var callbackStarted = false;
            try
            {
                return await atomic.ExecuteAtomicWriteAsync(async ct =>
                {
                    await tx.WriteAsync(async runner =>
                    {
                        foreach (var scope in scopes)
                        {
                            var cursor = await runner.RunAsync(IdentityDecisionQueries.AcquireScope, new { ownerScope = scope }).ConfigureAwait(false);
                            var rows = await cursor.ToListAsync().ConfigureAwait(false);
                            if (rows.Count != 1 || rows[0]["acquired"].As<long>() != 1)
                                throw new InvalidOperationException("Identity scope-lock infrastructure is missing or duplicated; migrate the owned schema.");
                        }
                        await ValidateRoots(runner, sourceIds, ownerId, includeShared, ct).ConfigureAwait(false);
                    }, ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    callbackStarted = true;
                    using var heldScopes = IdentityWriteScope.Enter(tx, scopes);
                    return await work(ct).ConfigureAwait(false);
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (TransientException ex) when (!callbackStarted && ex.IsRetriable)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (attempt >= 2) throw;
                // The atomic runner has confirmed rollback; unknown rollback is an AggregateException.
                // No ingestion callback/model/write outcome has begun, so acquisition alone can be retried.
                await Task.Delay(TimeSpan.FromMilliseconds(20 * (attempt + 1)), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<IdentityDecision>> ListPendingAsync(string? ownerId, int limit, string? afterDecisionId = null,
        CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        if (limit is < 1 or > 256) throw new ArgumentOutOfRangeException(nameof(limit), "A review page holds 1 to 256 decisions.");
        if (ownerId is not null) Text(ownerId);
        if (afterDecisionId is not null) Text(afterDecisionId);
        return await tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(IdentityDecisionQueries.ListPending,
                new { ownerScope = Scope(ownerId), after = afterDecisionId, limit }).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // The edge marks an open proposal; the stored record is the authority on its status.
            return (IReadOnlyList<IdentityDecision>)[.. rows.Select(row => IdentityDecisionPayload.Decode(row["identity_record_json"].As<string>()))
                .Where(decision => decision.Status == IdentityDecisionKind.Pending)];
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdentityDecision?> GetAsync(string decisionId, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken); Text(decisionId);
        if (ownerId is not null) Text(ownerId);
        return await tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(IdentityDecisionQueries.Get, new { decisionId, ownerScope = Scope(ownerId) }).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return rows.Count == 0 ? null : IdentityDecisionPayload.Decode(rows.Single()["identity_record_json"].As<string>());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdentityDecision> SaveAsync(IdentityDecisionWrite request, string? ownerId = null, CancellationToken cancellationToken = default)
    {
        Check(cancellationToken);
        var frozen = IdentityDecisionPayload.Capture(request, ownerId);
        if (frozen.Decision.Policy.IdentityNameComparison != StringComparison.Ordinal)
            throw new NotSupportedException("Atomic Neo4j identity currently guarantees Ordinal authority names only.");
        var reuseScopes = IdentityWriteScope.Reuse(tx, Scopes(ownerId, frozen.IncludeShared));
        return await tx.WriteAsync(async runner =>
        {
            if (!reuseScopes)
                foreach (var scope in Scopes(ownerId, frozen.IncludeShared).Order(StringComparer.Ordinal))
                    await runner.RunAsync(IdentityDecisionQueries.LockScope, new { ownerScope = scope }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var proposed = frozen.Decision;
            await ValidateRoots(runner, proposed.Context.SourceMessageIds.Concat(proposed.Observation.SourceMessageIds)
                .Distinct(StringComparer.Ordinal).ToArray(), ownerId, frozen.IncludeShared, cancellationToken).ConfigureAwait(false);
            var replayCursor = await runner.RunAsync(IdentityDecisionQueries.ReadReplay, new
            { decisionId = proposed.DecisionId, ownerScope = Scope(ownerId), observationKey = proposed.ObservationKey }).ConfigureAwait(false);
            var replay = await replayCursor.ToListAsync().ConfigureAwait(false);
            if (replay.Count > 1 || replay.Any(row => !row["authorized"].As<bool>()))
                throw new InvalidOperationException("The decision identifier/key conflicts with another observation or owner.");
            IdentityDecision current;
            if (replay.Count == 1)
            {
                current = IdentityDecisionPayload.Decode(replay[0]["identity_record_json"].As<string>());
                if (current.ObservationKey != proposed.ObservationKey || current.Observation.Name != proposed.Observation.Name ||
                    current.Observation.Type != proposed.Observation.Type || current.Observation.OwnerId != ownerId ||
                    !Roots(current).SequenceEqual(Roots(proposed), StringComparer.Ordinal))
                    throw new InvalidOperationException("An idempotent replay cannot replace the original observation or its source roots.");
                if (current.Status == IdentityDecisionKind.Confirmed)
                {
                    var candidate = await ReadCurrentTarget(runner, current, frozen.IncludeShared, cancellationToken).ConfigureAwait(false);
                    if (candidate is null || Snapshot(candidate, current.Policy) != replay[0]["identity_target_snapshot"].As<string>())
                        throw new InvalidOperationException("The reviewed target changed; explicit identity re-evaluation is required.");
                }
            }
            else
            {
                var validated = await Revalidate(runner, frozen, ownerId, cancellationToken).ConfigureAwait(false);
                current = validated.Decision;
                if (current.Status != IdentityDecisionKind.Confirmed)
                {
                    await MaterializeObservation(runner, current.Observation, current.ObservationKey, frozen.IncludeShared, cancellationToken).ConfigureAwait(false);
                    await Neo4jIdentityEvidenceRepository.AssertObservationContextAsync(runner, current, ownerId, cancellationToken).ConfigureAwait(false);
                }
                await runner.RunAsync(IdentityDecisionQueries.Create, new
                {
                    decisionId = current.DecisionId, ownerScope = Scope(ownerId), ownerId, observationKey = current.ObservationKey,
                    recordJson = IdentityDecisionPayload.Encode(current), targetSnapshot = validated.TargetSnapshot,
                    revisionId = current.DecisionId + "/1", createdAt = current.CreatedAtUtc.ToString("O")
                }).ConfigureAwait(false);
                if (current.PendingEntityIds.Count > 0)
                {
                    var proposals = await runner.RunAsync(IdentityDecisionQueries.Proposals, new
                    { decisionId = current.DecisionId, ownerScope = Scope(ownerId), ownerId, includeShared = frozen.IncludeShared, ids = current.PendingEntityIds }).ConfigureAwait(false);
                    if ((await proposals.SingleAsync().ConfigureAwait(false))["linked"].As<long>() != current.PendingEntityIds.Count)
                        throw new InvalidOperationException("A pending candidate left its permitted live scope before persistence.");
                }
            }
            var idCursor = await runner.RunAsync(IdentityDecisionQueries.BindingIds, new
            { decisionId = current.DecisionId, ownerScope = Scope(ownerId), limit = IdentityDecisionPayload.MaxBindings + 1 }).ConfigureAwait(false);
            var previousIds = (await idCursor.ToListAsync().ConfigureAwait(false)).Select(row => row["id"].As<string>()).ToHashSet(StringComparer.Ordinal);
            var bindings = frozen.Bindings.DistinctBy(binding => (binding.FactId, binding.Role)).ToArray();
            var ids = bindings.Select(binding => BindingId(current.DecisionId, binding)).ToArray();
            if (previousIds.Union(ids, StringComparer.Ordinal).Count() > IdentityDecisionPayload.MaxBindings)
                throw new InvalidOperationException("The decision exceeds its durable fact-binding bound.");
            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings[index];
                var cursor = await runner.RunAsync(IdentityDecisionQueries.Bind, new
                {
                    decisionId = current.DecisionId, ownerScope = Scope(ownerId), ownerId, includeShared = frozen.IncludeShared,
                    factId = binding.FactId, targetId = current.ConfirmedEntityId ?? current.Observation.EntityId,
                    role = binding.Role.ToString(), bindingId = ids[index], surface = current.Observation.Name,
                    surfaceKey = MemoryTripleCanonicalizer.CanonicalValue(current.Observation.Name),
                    sourceIds = proposed.Context.SourceMessageIds.Concat(proposed.Observation.SourceMessageIds).Distinct(StringComparer.Ordinal).ToArray(),
                    createdAt = current.CreatedAtUtc.ToString("O")
                }).ConfigureAwait(false);
                var rows = await cursor.ToListAsync().ConfigureAwait(false);
                if (rows.Count != 1) throw new InvalidOperationException("The fact role/provenance, binding or target is not valid in the exact requested scope.");
                cancellationToken.ThrowIfCancellationRequested();
            }
            cancellationToken.ThrowIfCancellationRequested();
            return current;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<(IdentityDecision Decision, string? TargetSnapshot)> Revalidate(IAsyncQueryRunner runner,
        IdentityDecisionWrite request, string? ownerId, CancellationToken cancellationToken)
    {
        var decision = request.Decision;
        var observation = decision.Observation;
        var mention = new ExtractedEntity { Name = observation.Name, Type = observation.Type, Description = observation.Description,
            Confidence = observation.Confidence, Attributes = observation.Attributes, Aliases = observation.Aliases };
        var query = Query(decision, request.IncludeShared);
        var literal = IdentityPolicy.IsCalendarLiteral(mention, decision.Context, decision.Policy, out var calendar);
        if (literal && calendar?.IsComplete != true)
        {
            var separate = decision with { Status = IdentityDecisionKind.New, ConfirmedEntityId = null, PendingEntityIds = [] };
            return (WithReceipt(separate, IdentityPolicy.Evaluate(mention, [], decision.Context, decision.Policy,
                ownerId is null ? SharedScopes.SharedOnly : MemoryScope.For(ownerId, request.IncludeShared)), [], true), null);
        }
        query = query with { CalendarKey = literal ? calendar!.CanonicalKey : null };
        var initialIds = request.ExpectedCandidates.Select(candidate => candidate.Entity.EntityId).Order(StringComparer.Ordinal).ToArray();
        if (initialIds.Length > 0)
        {
            var locks = await runner.RunAsync(IdentityDecisionQueries.LockCandidates, new
            { ids = initialIds, ownerId, includeShared = request.IncludeShared }).ConfigureAwait(false);
            await locks.ConsumeAsync().ConfigureAwait(false);
        }
        var exact = await Neo4jIdentityEvidenceRepository.FindExact(runner, query, cancellationToken).ConfigureAwait(false);
        var ids = exact.Candidates.Select(candidate => candidate.Entity.EntityId).Concat(initialIds).Distinct(StringComparer.Ordinal).Take(65).ToArray();
        if (ids.Length > 64) throw new InvalidOperationException("Fresh identity evidence exceeds the bounded decision plan; retry resolution.");
        var fresh = await Neo4jIdentityEvidenceRepository.Hydrate(runner, query, ids, new Dictionary<string, double>(), cancellationToken).ConfigureAwait(false);
        var scope = ownerId is null ? SharedScopes.SharedOnly : MemoryScope.For(ownerId, request.IncludeShared);
        // Scores retain only their original candidate-selection role. Reuse them solely when
        // the complete identity-relevant snapshot still matches; fresh vetoes always run first.
        var expectedById = request.ExpectedCandidates.ToDictionary(candidate => candidate.Entity.EntityId, StringComparer.Ordinal);
        var reviewed = fresh.Select(candidate => expectedById.TryGetValue(candidate.Entity.EntityId, out var prior) &&
            Snapshot(candidate, decision.Policy) == Snapshot(prior, decision.Policy)
                ? candidate with { Signals = prior.Signals } : candidate).ToArray();
        var evaluated = IdentityPolicy.Evaluate(mention, reviewed, decision.Context, decision.Policy, scope);
        var confirmed = fresh.SingleOrDefault(candidate => candidate.Entity.EntityId == decision.ConfirmedEntityId);
        var expected = request.ExpectedCandidates.SingleOrDefault(candidate => candidate.Entity.EntityId == decision.ConfirmedEntityId);
        var deterministic = evaluated.Decision == IdentityDecisionKind.Confirmed &&
            evaluated.ConfirmedEntityId == decision.ConfirmedEntityId;
        var contextual = !literal && ContextualTarget(decision) && evaluated.Decision == IdentityDecisionKind.Pending &&
            evaluated.CanAdjudicate && evaluated.PendingEntityIds.Contains(decision.ConfirmedEntityId!, StringComparer.Ordinal) &&
            MatchesSnapshots(fresh, CandidateSnapshots(request.ExpectedCandidates, decision.Policy), decision.Policy);
        if (decision.Status == IdentityDecisionKind.Confirmed && exact.ExactIdentityLookupComplete &&
            (deterministic || contextual) && confirmed is not null && expected is not null &&
            Snapshot(confirmed, decision.Policy) == Snapshot(expected, decision.Policy) &&
            (decision.ContextualEvidence is null || await ValidateContextualSources(runner, decision,
                request.IncludeShared, cancellationToken).ConfigureAwait(false)))
            return (WithReceipt(decision, evaluated, fresh, true), Snapshot(confirmed, decision.Policy));

        var rejected = evaluated.RejectedEntityIds.ToHashSet(StringComparer.Ordinal);
        var pending = evaluated.PendingEntityIds.Concat(evaluated.ConfirmedEntityId is { } id ? [id] : Array.Empty<string>())
            .Concat(decision.PendingEntityIds).Where(id => !rejected.Contains(id) && fresh.Any(candidate => candidate.Entity.EntityId == id))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var conservative = decision with
        {
            Status = pending.Length > 0 ? IdentityDecisionKind.Pending : IdentityDecisionKind.New,
            ConfirmedEntityId = null, PendingEntityIds = pending,
            Diagnostics = [.. decision.Diagnostics, "identity_revalidated_without_confirmation"]
        };
        return (WithReceipt(conservative, evaluated, fresh, exact.ExactIdentityLookupComplete), null);
    }

    private static async Task<IdentityCandidate?> ReadCurrentTarget(IAsyncQueryRunner runner, IdentityDecision decision,
        bool includeShared, CancellationToken cancellationToken)
    {
        var contextualSnapshots = decision.ContextualEvidence is null ? null : ReadContextualSnapshots(decision);
        if (decision.ContextualEvidence is not null && (contextualSnapshots is null ||
            !await ValidateContextualSources(runner, decision, includeShared, cancellationToken).ConfigureAwait(false))) return null;
        var historicalIds = contextualSnapshots?.Keys.ToArray() ?? [decision.ConfirmedEntityId!];
        var locks = await runner.RunAsync(IdentityDecisionQueries.LockCandidates, new
        { ids = historicalIds, ownerId = decision.Observation.OwnerId, includeShared }).ConfigureAwait(false);
        await locks.ConsumeAsync().ConfigureAwait(false);
        var query = Query(decision, includeShared);
        var mention = new ExtractedEntity { Name = decision.Observation.Name, Type = decision.Observation.Type,
            Description = decision.Observation.Description, Confidence = decision.Observation.Confidence };
        if (IdentityPolicy.IsCalendarLiteral(mention, decision.Context, decision.Policy, out var calendar))
        {
            if (calendar?.IsComplete != true) return null;
            query = query with { CalendarKey = calendar.CanonicalKey };
        }
        var exact = await Neo4jIdentityEvidenceRepository.FindExact(runner, query, cancellationToken).ConfigureAwait(false);
        if (!exact.ExactIdentityLookupComplete) return null;
        var ids = exact.Candidates.Select(candidate => candidate.Entity.EntityId).Concat(historicalIds)
            .Distinct(StringComparer.Ordinal).Take(65).ToArray();
        if (ids.Length > 64) return null;
        var candidates = await Neo4jIdentityEvidenceRepository.Hydrate(runner, query,
            ids, new Dictionary<string, double>(), cancellationToken).ConfigureAwait(false);
        if (contextualSnapshots is not null && !MatchesSnapshots(candidates, contextualSnapshots, decision.Policy)) return null;
        var scope = decision.Observation.OwnerId is null ? SharedScopes.SharedOnly : MemoryScope.For(decision.Observation.OwnerId, includeShared);
        var result = IdentityPolicy.Evaluate(mention, candidates, decision.Context, decision.Policy, scope);
        var target = candidates.SingleOrDefault(candidate => candidate.Entity.EntityId == decision.ConfirmedEntityId);
        if (result.Decision == IdentityDecisionKind.Confirmed && result.ConfirmedEntityId == decision.ConfirmedEntityId) return target;
        if (decision.LatestReview is { Action: IdentityReviewAction.Confirm } review && review.CandidateEntityId == decision.ConfirmedEntityId &&
            target is { EvidenceComplete: true } && !result.RejectedEntityIds.Contains(target.Entity.EntityId,StringComparer.Ordinal) &&
            result.Decision != IdentityDecisionKind.Confirmed)
            return target;
        // Only a previously committed contextual interpretation can use this replay path. Its
        // stored target snapshot is compared by the caller before any support is appended.
        return ContextualTarget(decision) && target is { EvidenceComplete: true } &&
            !IdentityPolicy.IsCalendarLiteral(mention, decision.Context, decision.Policy, out _) &&
            !result.RejectedEntityIds.Contains(target.Entity.EntityId, StringComparer.Ordinal) &&
            (result.Decision == IdentityDecisionKind.New || result.Decision == IdentityDecisionKind.Pending && result.CanAdjudicate)
                ? target : null;
    }

    /// <summary>
    /// The fingerprint a decision's target has now (<see cref="Snapshot"/>), or null when it is not visible. Owner import
    /// uses it: a decision whose stored fingerprint matched its source target gets its imported target's.
    /// </summary>
    internal static async Task<string?> TargetSnapshotAsync(IAsyncQueryRunner runner, IdentityDecision decision, string targetId,
        CancellationToken cancellationToken)
    {
        var target = (await Neo4jIdentityEvidenceRepository.Hydrate(runner, Query(decision, includeShared: true), [targetId],
            new Dictionary<string, double>(), cancellationToken).ConfigureAwait(false)).SingleOrDefault(candidate => candidate.Entity.EntityId == targetId);
        return target is null ? null : Snapshot(target, decision.Policy);
    }

    /// <summary>The fingerprints a contextual confirmation's candidates have now, by id; null when any is not visible.</summary>
    internal static async Task<Dictionary<string, string>?> CandidateSnapshotsAsync(IAsyncQueryRunner runner, IdentityDecision decision,
        IReadOnlyCollection<string> ids, CancellationToken cancellationToken)
    {
        var candidates = await Neo4jIdentityEvidenceRepository.Hydrate(runner, Query(decision, includeShared: true), [.. ids],
            new Dictionary<string, double>(), cancellationToken).ConfigureAwait(false);
        return candidates.Count == ids.Count ? CandidateSnapshots(candidates, decision.Policy) : null;
    }

    private static Dictionary<string, string> CandidateSnapshots(IReadOnlyList<IdentityCandidate> candidates,
        EntityIdentityPolicy policy) => candidates.ToDictionary(candidate => candidate.Entity.EntityId,
            candidate => Snapshot(candidate, policy), StringComparer.Ordinal);

    private static bool MatchesSnapshots(IReadOnlyList<IdentityCandidate> candidates,
        IReadOnlyDictionary<string, string> snapshots, EntityIdentityPolicy policy) => candidates.Count == snapshots.Count &&
        candidates.All(candidate => snapshots.TryGetValue(candidate.Entity.EntityId, out var expected) &&
            Snapshot(candidate, policy) == expected);

    internal static Dictionary<string, string>? ReadContextualSnapshots(IdentityDecision decision)
    {
        // Only called for an already committed decision read from storage. Initial caller JSON
        // is wrapped under suppliedReceipt and never read as backend confirmation authority.
        using var receipt = JsonDocument.Parse(decision.EvidenceJson, new() { MaxDepth = 16 });
        if (!receipt.RootElement.TryGetProperty("contextualCandidateSnapshots", out var values) ||
            values.ValueKind != JsonValueKind.Object) return null;
        var snapshots = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in values.EnumerateObject())
        {
            if (snapshots.Count >= 64 || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 4096 ||
                entry.Value.ValueKind != JsonValueKind.String || entry.Value.GetString() is not { Length: 64 } hash ||
                !hash.All(Uri.IsHexDigit) || !snapshots.TryAdd(entry.Name, hash)) return null;
        }
        return decision.ConfirmedEntityId is not null && snapshots.ContainsKey(decision.ConfirmedEntityId) ? snapshots : null;
    }

    private static bool ContextualTarget(IdentityDecision decision) => decision.Policy.AllowContextualConfirmation &&
        decision.ContextualEvidence is { Verdict: IdentityAdjudicationVerdict.Same, CandidateId: { } id, Citations.Count: > 0 } &&
        id == decision.ConfirmedEntityId;

    private static async Task<bool> ValidateContextualSources(IAsyncQueryRunner runner, IdentityDecision decision,
        bool includeShared, CancellationToken cancellationToken)
    {
        var sources = decision.Context.Sources.DistinctBy(source => source.SourceId).ToArray();
        // The ingestion adapter offers complete message fragments. Aggregated/generated source
        // fragments cannot acquire model authority without a separate grounding contract.
        if (sources.Length is 0 or > 32 || sources.Any(source =>
            source.OriginIds.Count != 1 || source.OriginIds[0] != source.SourceId)) return false;
        var cursor = await runner.RunAsync(IdentityDecisionQueries.ValidateContextualSources, new
        {
            sources = sources.Select(source => new Dictionary<string, object> { ["id"] = source.SourceId, ["text"] = source.Text }).ToArray(),
            ownerId = decision.Observation.OwnerId, includeShared
        }).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return rows.Count == sources.Length && rows.All(row => row["valid"].As<bool>()) &&
            rows.Select(row => row["sourceId"].As<string>()).ToHashSet(StringComparer.Ordinal)
                .SetEquals(sources.Select(source => source.SourceId));
    }

    private static IdentityDecision WithReceipt(IdentityDecision decision, IdentityPolicyResult result,
        IReadOnlyList<IdentityCandidate> candidates, bool exactComplete)
    {
        using var supplied = JsonDocument.Parse(decision.EvidenceJson, new() { MaxDepth = 16 });
        var receipt = IdentityDecisionPayload.Serialize(new
        {
            persistedDecision = decision.Status.ToString(),
            // Backend-owned metadata is generated here, never copied from the supplied receipt.
            contextualCandidateSnapshots = decision.Status == IdentityDecisionKind.Confirmed && decision.ContextualEvidence is not null
                ? CandidateSnapshots(candidates, decision.Policy) : null,
            suppliedReceipt = supplied.RootElement,
            revalidation = new { decision = result.Decision.ToString(), confirmedEntityId = result.ConfirmedEntityId,
                pendingEntityIds = result.PendingEntityIds, rejectedEntityIds = result.RejectedEntityIds,
                reasons = result.Reasons, exactLookupComplete = exactComplete },
            candidates = candidates.Select(candidate => new
            {
                id = candidate.Entity.EntityId, name = candidate.Entity.Name, type = candidate.Entity.Type,
                ownerId = candidate.Entity.OwnerId, canonicalName = candidate.Entity.CanonicalName,
                description = candidate.Entity.Description, sourceIds = candidate.Entity.SourceMessageIds,
                verifiedKeys = candidate.VerifiedKeys, verifiedAliases = candidate.VerifiedAliases,
                verifiedKind = candidate.VerifiedKind, nameNamespace = candidate.NameNamespace,
                evidenceVersion = candidate.EvidenceVersion, evidenceComplete = candidate.EvidenceComplete,
                contextProperties = decision.Policy.ContextProperties.Order(StringComparer.Ordinal).ToDictionary(name => name,
                    name => candidate.Entity.Attributes.GetValueOrDefault(name), StringComparer.Ordinal)
            }).ToArray()
        });
        return decision with { EvidenceJson = Encoding.UTF8.GetString(receipt) };
    }

    private static IdentityCandidateQuery Query(IdentityDecision decision, bool includeShared) => new()
    {
        Name = decision.Observation.Name, Type = decision.Observation.Type, CompatibleTypes = decision.Policy.CompatibleTypes,
        OwnerId = decision.Observation.OwnerId, IncludeShared = includeShared, Limit = 64,
        VerifiedKeys = IdentityPolicy.CompleteKeys(decision.Context.VerifiedKeys, decision.Policy),
        IdentityProperties = decision.Policy.IdentityProperties, NameNamespace = decision.Context.NameNamespace,
        UseFuzzyCandidates = true
    };

    private static string Snapshot(IdentityCandidate candidate, EntityIdentityPolicy policy) => Convert.ToHexString(SHA256.HashData(
        IdentityDecisionPayload.Serialize(new
        {
            candidate.Entity.EntityId, candidate.Entity.OwnerId, candidate.Entity.Name, candidate.Entity.CanonicalName,
            candidate.Entity.Type, candidate.Entity.Description, candidate.EvidenceVersion, candidate.EvidenceComplete,
            sourceIds = candidate.Entity.SourceMessageIds.Order(StringComparer.Ordinal).ToArray(),
            context = policy.ContextProperties.Order(StringComparer.Ordinal).ToDictionary(name => name,
                name => candidate.Entity.Attributes.GetValueOrDefault(name), StringComparer.Ordinal)
        })));

    private static string[] Roots(IdentityDecision decision)
    {
        var ids = decision.Context.SourceMessageIds.Concat(decision.Observation.SourceMessageIds).Distinct(StringComparer.Ordinal);
        return ids.SelectMany(id => decision.Context.Sources.FirstOrDefault(source => source.SourceId == id) is { OriginIds.Count: > 0 } fragment
            ? fragment.OriginIds : (IReadOnlyList<string>)[id]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task MaterializeObservation(IAsyncQueryRunner runner, Entity observation, string observationKey, bool includeShared, CancellationToken cancellationToken)
    {
        var properties = new Dictionary<string, object?>
        {
            ["owner_id"] = observation.OwnerId, ["owner_key"] = OwnerStorageKey.Encode(observation.OwnerId), ["name"] = observation.Name,
            ["identity_observation_key"] = observationKey,
            ["identity_calendar_key"] = CalendarValues.IsExplicitTemporalType(observation.Type) &&
                CalendarValues.TryRead(observation.Name, observation.Type, out var intrinsicCalendar) && intrinsicCalendar?.IsComplete == true
                ? intrinsicCalendar.CanonicalKey : null,
            ["type"] = observation.Type, ["canonical_name"] = observation.CanonicalName, ["confidence"] = observation.Confidence,
            ["description"] = observation.Description, ["aliases"] = observation.Aliases,
            ["source_message_ids"] = observation.SourceMessageIds, ["created_at"] = observation.CreatedAtUtc.ToString("O"),
            ["attributes"] = JsonSerializer.Serialize(observation.Attributes), ["metadata"] = JsonSerializer.Serialize(observation.Metadata),
            ["embedding"] = observation.Embedding?.Select(value => (double)value).ToArray()
        };
        var cursor = await runner.RunAsync(IdentityDecisionQueries.CreateObservation, new
        { entityId = observation.EntityId, observationKey, ownerId = observation.OwnerId, name = observation.Name, type = observation.Type,
            sourceIds = observation.SourceMessageIds, includeShared, properties }).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count != 1 || !rows[0]["valid"].As<bool>()) throw new InvalidOperationException("The reserved observation ID collides with another entity.");
    }

    private static async Task ValidateRoots(IAsyncQueryRunner runner, IReadOnlyList<string> sourceIds,
        string? ownerId, bool includeShared, CancellationToken cancellationToken)
    {
        var cursor = await runner.RunAsync(IdentityDecisionQueries.ValidateRoots, new { sourceIds, ownerId, includeShared }).ConfigureAwait(false);
        var rows = await cursor.ToListAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (rows.Count != 1 || !rows[0]["valid"].As<bool>())
            throw new InvalidOperationException("An identity source root belongs to an unauthorized conversation.");
    }

    private void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsEnabled) throw new InvalidOperationException("Enable and migrate identity-evidence before using durable identity decisions.");
    }
    private static string BindingId(string decisionId, IdentityFactBinding binding) => BindingId(decisionId, binding.FactId, binding.Role.ToString());

    /// <summary>A binding's id follows its decision, fact and role; owner import derives it the same way (REV1-S1).</summary>
    internal static string BindingId(string decisionId, string factId, string role) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { decisionId, factId, role }))));
    private static string Scope(string? ownerId) => ownerId is null ? "shared" : "owned:" + ownerId;
    private static IEnumerable<string> Scopes(string? ownerId, bool includeShared) => ownerId is not null && includeShared ? [Scope(ownerId), Scope(null)] : [Scope(ownerId)];
    private static void Text(string? value)
    { if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new ArgumentException("Identity text must contain 1–4096 characters."); }
}
