using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Memory;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Repositories;

internal sealed partial class Neo4jIdentityDecisionRepository
{
    public async Task<IReadOnlyList<IdentityDecision>> GetHistoryAsync(string decisionId,string? ownerId=null,
        int limit=32,long? beforeVersion=null,CancellationToken cancellationToken=default)
    {
        Check(cancellationToken); Text(decisionId); if(ownerId is not null) Text(ownerId);
        if(limit is <1 or >64 || beforeVersion is <1) throw new ArgumentOutOfRangeException(nameof(limit));
        return await tx.ReadAsync(async runner=>
        {
            var cursor=await runner.RunAsync(IdentityReviewQueries.History,new
            { decisionId,ownerScope=Scope(ownerId),limit,beforeVersion }).ConfigureAwait(false);
            var rows=await cursor.ToListAsync().ConfigureAwait(false); cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<IdentityDecision>)rows.Select(row=>IdentityDecisionPayload.Decode(row["recordJson"].As<string>())).ToArray();
        },cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdentityReviewResult> ReviewAsync(IdentityReviewRequest request,string? ownerId=null,
        bool includeShared=false,CancellationToken cancellationToken=default)
    {
        Check(cancellationToken);
        var frozen=IdentityReviewPayload.Capture(request,ownerId);
        var requestHash=IdentityReviewPayload.Fingerprint(frozen,ownerId,includeShared);
        var revisionId=IdentityReviewPayload.RevisionId(frozen,ownerId);
        var reuse=IdentityWriteScope.Reuse(tx,Scopes(ownerId,includeShared));
        return await tx.WriteAsync(async runner=>
        {
            if(!reuse)
                foreach(var scope in Scopes(ownerId,includeShared).Order(StringComparer.Ordinal))
                {
                    var locked=await runner.RunAsync(IdentityDecisionQueries.LockScope,new { ownerScope=scope }).ConfigureAwait(false);
                    await locked.ConsumeAsync().ConfigureAwait(false);
                }
            var currentCursor=await runner.RunAsync(IdentityRelationshipQueries.ReadEndpointDecision,
                new { decisionId=frozen.DecisionId,ownerScope=Scope(ownerId) }).ConfigureAwait(false);
            var currentRows=await currentCursor.ToListAsync().ConfigureAwait(false);
            if(currentRows.Count!=1) throw new InvalidOperationException("The review decision is not present in the exact owner scope.");
            var current=IdentityDecisionPayload.Decode(currentRows[0]["recordJson"].As<string>());
            var operationCursor=await runner.RunAsync(IdentityReviewQueries.GetOperation,
                new { revisionId,ownerScope=Scope(ownerId) }).ConfigureAwait(false);
            var operations=await operationCursor.ToListAsync().ConfigureAwait(false);
            if(operations.Count>0)
            {
                var applied=IdentityDecisionPayload.Decode(operations.Single()["recordJson"].As<string>());
                if(applied.DecisionId!=current.DecisionId || applied.LatestReview?.RequestHash!=requestHash ||
                    applied.LatestReview.OperationId!=frozen.OperationId)
                    throw new InvalidOperationException("The operation key was already used for a different review request.");
                return new() { CurrentDecision=current,AppliedRevision=applied,AlreadyApplied=true };
            }
            if(current.Version!=frozen.ExpectedVersion) throw new InvalidOperationException("The identity review version is stale; read the current revision.");
            cancellationToken.ThrowIfCancellationRequested();
            IdentityDecision next; string? targetSnapshot=null;
            if(frozen.Action==IdentityReviewAction.Confirm)
            {
                var selected=await ValidateReviewTarget(runner,current,frozen.CandidateEntityId!,ownerId,includeShared,cancellationToken).ConfigureAwait(false);
                targetSnapshot=Snapshot(selected,current.Policy);
                next=current with { Status=IdentityDecisionKind.Confirmed,ConfirmedEntityId=selected.Entity.EntityId,PendingEntityIds=[],ContextualEvidence=null };
            }
            else if(frozen.Action==IdentityReviewAction.Reject)
            {
                if(current.Status!=IdentityDecisionKind.Pending || !current.PendingEntityIds.Contains(frozen.CandidateEntityId!,StringComparer.Ordinal))
                    throw new InvalidOperationException("Only a currently pending candidate can be rejected.");
                var remaining=current.PendingEntityIds.Where(id=>id!=frozen.CandidateEntityId).ToArray();
                var pending=await FreshReviewProposals(runner,current,remaining,ownerId,includeShared,cancellationToken).ConfigureAwait(false);
                next=current with { Status=pending.Length>0?IdentityDecisionKind.Pending:IdentityDecisionKind.New,
                    ConfirmedEntityId=null,PendingEntityIds=pending,ContextualEvidence=null };
            }
            else
            {
                if(current.Status!=IdentityDecisionKind.Confirmed) throw new InvalidOperationException("Only a confirmed interpretation can be revoked.");
                next=current with { Status=IdentityDecisionKind.New,ConfirmedEntityId=null,PendingEntityIds=[],ContextualEvidence=null };
            }
            var now=(clock??TimeProvider.System).GetUtcNow();
            next=next with
            {
                Version=checked(current.Version+1),UpdatedAtUtc=now,
                LatestReview=new() { OperationId=frozen.OperationId,RequestHash=requestHash,Action=frozen.Action,
                    CandidateEntityId=frozen.CandidateEntityId,Actor=frozen.Actor,Reason=frozen.Reason,
                    PreviousVersion=current.Version,Version=current.Version+1,PerformedAtUtc=now },
                Diagnostics=current.Diagnostics.Append("identity_review_"+frozen.Action.ToString().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray(),
                EvidenceJson=System.Text.Json.JsonSerializer.Serialize(new { authority="explicit_host_review",requestHash,
                    actor=frozen.Actor,reason=frozen.Reason,action=frozen.Action.ToString(),target=frozen.CandidateEntityId })
            };
            if(next.Status!=IdentityDecisionKind.Confirmed)
                await MaterializeObservation(runner,next.Observation,next.ObservationKey,includeShared,cancellationToken).ConfigureAwait(false);
            var targetId=next.ConfirmedEntityId??next.Observation.EntityId;
            var targetCursor=await runner.RunAsync(IdentityReviewQueries.SetDecisionTarget,new
            { decisionId=next.DecisionId,ownerScope=Scope(ownerId),ownerId,includeShared,targetId }).ConfigureAwait(false);
            if((await targetCursor.ToListAsync().ConfigureAwait(false)).Count!=1)
                throw new InvalidOperationException("The reviewed target cannot be linked safely in the requested scope.");
            await RewireReviewBindings(runner,next,targetId,ownerId,includeShared,cancellationToken).ConfigureAwait(false);
            var proposals=await runner.RunAsync(IdentityReviewQueries.ClearProposals,
                new { decisionId=next.DecisionId,ownerScope=Scope(ownerId) }).ConfigureAwait(false);
            await proposals.ConsumeAsync().ConfigureAwait(false);
            if(next.PendingEntityIds.Count>0)
            {
                var pending=await runner.RunAsync(IdentityDecisionQueries.Proposals,
                    new { decisionId=next.DecisionId,ownerScope=Scope(ownerId),ownerId,includeShared,ids=next.PendingEntityIds }).ConfigureAwait(false);
                if((await pending.SingleAsync().ConfigureAwait(false))["linked"].As<long>()!=next.PendingEntityIds.Count)
                    throw new InvalidOperationException("A pending alternative left its permitted live scope during review.");
            }
            var committed=await runner.RunAsync(IdentityReviewQueries.CommitRevision,new
            {
                decisionId=next.DecisionId,ownerScope=Scope(ownerId),ownerId,expectedVersion=frozen.ExpectedVersion,
                version=next.Version,recordJson=IdentityDecisionPayload.Encode(next),targetSnapshot,revisionId,reviewedAt=now.ToString("O")
            }).ConfigureAwait(false);
            if((await committed.ToListAsync().ConfigureAwait(false)).Count!=1)
                throw new InvalidOperationException("The review lost its expected version; no support change is committed.");
            cancellationToken.ThrowIfCancellationRequested();
            return new IdentityReviewResult { CurrentDecision=next,AppliedRevision=next,AlreadyApplied=false };
        },cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IdentityCandidate> ValidateReviewTarget(IAsyncQueryRunner runner,IdentityDecision decision,
        string targetId,string? ownerId,bool includeShared,CancellationToken cancellationToken)
    {
        if(targetId==decision.Observation.EntityId) throw new InvalidOperationException("A confirmation must select a separate existing identity.");
        await ValidateRoots(runner,Roots(decision),ownerId,includeShared,cancellationToken).ConfigureAwait(false);
        var locked=await runner.RunAsync(IdentityDecisionQueries.LockCandidates,
            new { ids=new[] { targetId },ownerId,includeShared }).ConfigureAwait(false);
        await locked.ConsumeAsync().ConfigureAwait(false);
        var query=Query(decision,includeShared);
        var mention=new ExtractedEntity { Name=decision.Observation.Name,Type=decision.Observation.Type,
            Description=decision.Observation.Description,Attributes=decision.Observation.Attributes,Confidence=decision.Observation.Confidence };
        var literal=IdentityPolicy.IsCalendarLiteral(mention,decision.Context,decision.Policy,out var calendar);
        if(literal && calendar?.IsComplete!=true) throw new InvalidOperationException("Incomplete or invalid calendar values cannot be confirmed as an identity.");
        query=query with { CalendarKey=literal?calendar!.CanonicalKey:null };
        var exact=await Neo4jIdentityEvidenceRepository.FindExact(runner,query,cancellationToken).ConfigureAwait(false);
        if(!exact.ExactIdentityLookupComplete) throw new InvalidOperationException("The exact identity universe is incomplete.");
        var ids=exact.Candidates.Select(candidate=>candidate.Entity.EntityId).Append(targetId)
            .Where(id=>id!=decision.Observation.EntityId).Distinct(StringComparer.Ordinal).Take(65).ToArray();
        if(ids.Length>64) throw new InvalidOperationException("The review exceeds its bounded identity universe.");
        var candidates=await Neo4jIdentityEvidenceRepository.Hydrate(runner,query,ids,new Dictionary<string,double>(),cancellationToken).ConfigureAwait(false);
        var selected=candidates.SingleOrDefault(candidate=>candidate.Entity.EntityId==targetId);
        if(selected is null || !selected.EvidenceComplete || candidates.Any(candidate=>!candidate.EvidenceComplete))
            throw new InvalidOperationException("The selected identity is missing, retired, out of scope or has incomplete evidence.");
        var scope=ownerId is null?SharedScopes.SharedOnly:MemoryScope.For(ownerId,includeShared);
        var result=IdentityPolicy.Evaluate(mention,candidates,decision.Context,decision.Policy,scope);
        if(result.RejectedEntityIds.Contains(targetId,StringComparer.Ordinal) ||
            result.Decision==IdentityDecisionKind.Confirmed && result.ConfirmedEntityId!=targetId ||
            literal && (result.Decision!=IdentityDecisionKind.Confirmed || result.ConfirmedEntityId!=targetId))
            throw new InvalidOperationException("Explicit review cannot override a domain contradiction or a different calendar value.");
        return selected;
    }

    private static async Task<string[]> FreshReviewProposals(IAsyncQueryRunner runner,IdentityDecision decision,
        string[] ids,string? ownerId,bool includeShared,CancellationToken cancellationToken)
    {
        if(ids.Length==0) return [];
        // Normal entity writers need not take the identity scope lock. Keep visible rows locked
        // through the fresh read and proposal write; a withdrawn alternative cannot be reasserted.
        var locked=await runner.RunAsync(IdentityDecisionQueries.LockCandidates,new { ids,ownerId,includeShared }).ConfigureAwait(false);
        await locked.ConsumeAsync().ConfigureAwait(false);
        var candidates=await Neo4jIdentityEvidenceRepository.Hydrate(runner,Query(decision,includeShared),
            ids,new Dictionary<string,double>(),cancellationToken).ConfigureAwait(false);
        var mention=new ExtractedEntity { Name=decision.Observation.Name,Type=decision.Observation.Type,
            Description=decision.Observation.Description,Attributes=decision.Observation.Attributes,Confidence=decision.Observation.Confidence };
        var scope=ownerId is null?SharedScopes.SharedOnly:MemoryScope.For(ownerId,includeShared);
        var result=IdentityPolicy.Evaluate(mention,candidates,decision.Context,decision.Policy,scope);
        cancellationToken.ThrowIfCancellationRequested();
        // Rejecting another candidate cannot implicitly confirm an alternative. Retain only
        // still-visible, live, type-compatible alternatives without a fresh deterministic veto.
        return candidates.Select(candidate=>candidate.Entity.EntityId)
            .Where(id=>!result.RejectedEntityIds.Contains(id,StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
    }

    private static async Task RewireReviewBindings(IAsyncQueryRunner runner,IdentityDecision decision,string targetId,
        string? ownerId,bool includeShared,CancellationToken cancellationToken)
    {
        var cursor=await runner.RunAsync(IdentityReviewQueries.ReadBindings,new
        { decisionId=decision.DecisionId,ownerScope=Scope(ownerId),limit=IdentityDecisionPayload.MaxBindings+1 }).ConfigureAwait(false);
        var rows=await cursor.ToListAsync().ConfigureAwait(false);
        if(rows.Count>IdentityDecisionPayload.MaxBindings) throw new InvalidOperationException("The reviewed decision exceeds its support bound.");
        foreach(var row in rows)
        {
            var facts=row["facts"].As<List<string>>();
            if(facts.Count!=1 || !Enum.TryParse<IdentityFactRole>(row["role"].As<string>(),out var role) || !Enum.IsDefined(role))
                throw new InvalidOperationException("A reviewed binding has invalid fact ownership or role structure.");
            var bindingId=row["id"].As<string>();
            var clear=await runner.RunAsync(IdentityReviewQueries.ClearBindingTarget,new
            { decisionId=decision.DecisionId,ownerScope=Scope(ownerId),ownerId,bindingId }).ConfigureAwait(false);
            if((await clear.ToListAsync().ConfigureAwait(false)).Count!=1)
                throw new InvalidOperationException("Only this decision's valid owned support can be rewired.");
            var linked=await runner.RunAsync(IdentityDecisionQueries.Bind,new
            {
                decisionId=decision.DecisionId,ownerScope=Scope(ownerId),ownerId,includeShared,
                factId=facts[0],targetId,role=role.ToString(),bindingId,surface=decision.Observation.Name,
                surfaceKey=MemoryTripleCanonicalizer.CanonicalValue(decision.Observation.Name),
                sourceIds=Roots(decision),createdAt=decision.CreatedAtUtc.ToString("O")
            }).ConfigureAwait(false);
            if((await linked.ToListAsync().ConfigureAwait(false)).Count!=1)
                throw new InvalidOperationException("The reviewed fact role or provenance is no longer valid.");
            cancellationToken.ThrowIfCancellationRequested();
        }
    }
}
