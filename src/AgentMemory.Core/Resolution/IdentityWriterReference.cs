using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Memory;

namespace AgentMemory.Core.Resolution;

/// <summary>
/// Re-checks a store-aware writer's stored-entity reference (<see cref="ExtractedEntity.Reference"/>) as contextual
/// evidence. The writer saw the stored entity and the current turn together; this step records that judgment instead of
/// re-deciding from less evidence. It makes no model call and no I/O, never overrides a veto or a deterministic
/// decision, refuses ambiguous candidates and quotes the current message does not contain, and lets the type policy
/// decide whether a supported reference confirms (<see cref="EntityIdentityPolicy.AllowContextualConfirmation"/>) or is
/// kept for review. Anything it refuses falls through to the adjudicator unchanged.
/// </summary>
internal static class IdentityWriterReference
{
    public static IdentityResolutionPlan Apply(ExtractedEntity mention, IdentityResolutionPlan plan, EntityIdentityPolicy policy)
    {
        if (mention.Reference is not { } reference) return plan;
        IdentityResolutionPlan Note(string diagnostic) => plan with { Diagnostics = [.. plan.Diagnostics, diagnostic] };
        var result = plan.Result;
        if (result.Decision == IdentityDecisionKind.Confirmed)
            return Note(string.Equals(result.ConfirmedEntityId, reference.EntityId, StringComparison.Ordinal)
                ? "writer_reference_agrees" : "writer_reference_conflicts");
        var allowed = result.PendingEntityIds.Except(result.RejectedEntityIds, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        if (result.Decision != IdentityDecisionKind.Pending || !allowed.Contains(reference.EntityId))
            return Note("writer_reference_not_candidate");
        // Incomplete exact lookups, several confirmations or incomplete evidence: a model judgment cannot settle them.
        if (!result.CanAdjudicate) return Note("writer_reference_not_eligible");
        var targetCandidate = plan.Candidates.FirstOrDefault(candidate => candidate.Entity.EntityId == reference.EntityId);
        var target = targetCandidate?.Entity;
        if (target is null) return Note("writer_reference_not_candidate");
        var name = MemoryTripleCanonicalizer.CanonicalValue(target.Name);
        if (plan.Candidates.Any(candidate => candidate.Entity.EntityId != target.EntityId && allowed.Contains(candidate.Entity.EntityId) &&
            MemoryTripleCanonicalizer.CanonicalValue(candidate.Entity.Name) == name))
            return Note("writer_reference_ambiguous");
        // IDENTITY-HARD-CASES D, the paid check of 24 Nov (ihc1-v2-glm-3, statement 113): two people of one first name stored
        // as "Kiran" and "grown-up Kiran"; "Kiran was on call the first night", which only earlier facts decide, was referred
        // to the wrong one and confirmed. A bare name another candidate's name holds says nothing of which one: the writer's
        // guess is kept for review, a split rather than a merge.
        if (plan.Candidates.Any(candidate => candidate.Entity.EntityId != target.EntityId &&
            string.Equals(candidate.Entity.Type, target.Type, StringComparison.OrdinalIgnoreCase) &&
            MemoryTripleCanonicalizer.CanonicalValue(mention.Name) == name && IdentityPolicy.HoldsName(candidate.Entity.Name, mention.Name)))
            return Note("writer_reference_ambiguous");
        var roots = plan.Context.SourceMessageIds.ToHashSet(StringComparer.Ordinal);
        var source = string.IsNullOrWhiteSpace(reference.Quote) || reference.Quote.Length > 2_048 ? null :
            plan.Context.Sources.FirstOrDefault(fragment => roots.Contains(fragment.SourceId) &&
                fragment.Text.Contains(reference.Quote, StringComparison.Ordinal));
        if (source is null) return Note("writer_reference_unsupported_quote");
        // IDENTITY-NAMESAKE-WRITER-MERGE (IKB-3): the writer referred "my niece Valentina" to the stored designer Valentina
        // Ríos. The quote was there and she was the only Valentina, yet the turn's own words describe someone else. A relation
        // the turn gives that the stored person is not known by, when they are known by another, is no agreement: refused
        // here like any other unsupported reference, so it stays for review or the adjudicator.
        // IDENTITY-NICKNAME-SPLIT: a person the writer refers to under a short form ("Bea") comes under the stored name with
        // the short form as an alias; the turn names the alias, so its relation is read there too ("my niece Bea").
        var turnText = string.Join("\n", plan.Context.Sources.Where(fragment => roots.Contains(fragment.SourceId)).Select(fragment => fragment.Text));
        if (IdentityRelationCue.Disagree(IdentityRelationCue.InTurn(turnText, [mention.Name, target.Name, .. mention.Aliases]),
                IdentityRelationCue.Known(targetCandidate!)))
            return Note("writer_reference_relation_conflict");

        var evidence = new IdentityContextualResult(IdentityContextualVerdict.Same, target.EntityId,
            [new IdentityEvidenceQuote(source.SourceId, reference.Quote)]);
        if (!policy.AllowContextualConfirmation)
            return plan with
            {
                Result = result with { CanAdjudicate = false },
                Diagnostics = [.. plan.Diagnostics, "writer_reference_requires_review"], ContextualEvidence = evidence
            };
        var reasons = result.Reasons.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        reasons[target.EntityId] = "writer_reference";
        return plan with
        {
            Entity = target, Created = false,
            Result = result with { Decision = IdentityDecisionKind.Confirmed, ConfirmedEntityId = target.EntityId,
                PendingEntityIds = [], CanAdjudicate = false, Reasons = reasons },
            Diagnostics = [.. plan.Diagnostics, "writer_reference_confirmed"], ContextualEvidence = evidence
        };
    }
}
