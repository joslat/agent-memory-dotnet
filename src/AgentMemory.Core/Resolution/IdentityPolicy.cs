using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Services;

namespace AgentMemory.Core.Resolution;

/// <summary>Deterministic identity authority, separate from retrieval and candidate scoring.</summary>
internal static class IdentityPolicy
{
    /// <summary>Review reason of a candidate nominated only because it was observed in a context turn.</summary>
    internal const string ContextRootedReview = "context_rooted_review";

    internal static IdentityPolicyResult Evaluate(ExtractedEntity mention,
        IReadOnlyList<IdentityCandidate> candidates, EntityIdentityResolutionContext context,
        EntityIdentityPolicy policy, MemoryScope? scope)
    {
        var rejected = new HashSet<string>(StringComparer.Ordinal);
        var review = new HashSet<string>(StringComparer.Ordinal);
        var confirmed = new HashSet<string>(StringComparer.Ordinal);
        var incomplete = new HashSet<string>(StringComparer.Ordinal);
        var reasons = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // A verified host key identifies a reference even when an OBJECT's display name looks
        // like a date. Explicit temporal types remain values; their keys cannot override a date.
        var literal = IsCalendarLiteral(mention, context, policy, out var calendar);
        var mentionKind = MentionKind(mention, context, policy);
        if (literal && calendar?.IsComplete != true)
            return new(IdentityDecisionKind.New, null, [], [], false, reasons);

        foreach (var candidate in candidates)
        {
            var entity = candidate.Entity;
            var id = entity.EntityId;
            // A backend returning an out-of-scope row must not expose even its ID in a decision.
            if (!Visible(entity, scope)) continue;
            if (rejected.Contains(id)) continue;
            if (!candidate.EvidenceComplete) incomplete.Add(id);
            string? veto = !literal && !CompatibleType(mention.Type, entity.Type, policy) ? "different_type" :
                !ValidSignals(candidate.Signals) ? "invalid_signal" : null;
            if (veto is not null)
            {
                rejected.Add(id);
                reasons[id] = veto;
                continue;
            }
            if (literal)
            {
                var kind = CandidateKind(candidate);
                if (kind == IdentitySubjectKind.Reference ||
                    mentionKind != IdentitySubjectKind.Unknown && kind != IdentitySubjectKind.Unknown && mentionKind != kind)
                {
                    rejected.Add(id);
                    reasons[id] = "different_subject_kind";
                }
                else if (CalendarMatches(candidate, kind, calendar!))
                {
                    if (mentionKind == IdentitySubjectKind.Unknown || kind == IdentitySubjectKind.Unknown)
                    {
                        review.Add(id);
                        reasons[id] = "calendar_kind_unverified";
                    }
                    else
                    {
                        confirmed.Add(id);
                        reasons[id] = "calendar_value_equal";
                    }
                }
                else
                {
                    rejected.Add(id);
                    reasons[id] = "calendar_value_different_or_unknown";
                }
                continue;
            }
            var candidateKind = CandidateKind(candidate);
            if (candidateKind is IdentitySubjectKind.CalendarDate or IdentitySubjectKind.CalendarInterval)
            {
                rejected.Add(id);
                reasons[id] = "entity_is_calendar_value";
                continue;
            }

            var ownKeys = context.VerifiedKeys.Where(key => Complete(key, policy)).ToList();
            var candidateKeys = candidate.VerifiedKeys.Where(key => Complete(key, policy)).ToList();
            var pairs = ownKeys.SelectMany(left => candidateKeys
                .Where(right => string.Equals(left.Namespace, right.Namespace, StringComparison.Ordinal))
                .Select(right => (Left: left, Right: right))).ToList();
            bool EqualKey((VerifiedIdentityKey Left, VerifiedIdentityKey Right) pair) =>
                policy.IdentityProperties.All(property =>
                    string.Equals(Component(pair.Left, property), Component(pair.Right, property), StringComparison.Ordinal));
            // A conflicting active key is a veto even when another key/name/score appears to agree.
            if (pairs.Any(pair => !EqualKey(pair)))
            {
                rejected.Add(id);
                reasons[id] = "verified_key_conflict";
                continue;
            }
            var exactName = SameName(mention.Name, entity.Name);
            var uniqueName = !string.IsNullOrWhiteSpace(policy.UniqueNameNamespace) && ProofName(mention.Name, entity.Name, policy) &&
                string.Equals(context.NameNamespace, policy.UniqueNameNamespace, StringComparison.Ordinal) &&
                string.Equals(candidate.NameNamespace, policy.UniqueNameNamespace, StringComparison.Ordinal);
            var backedAlias = !string.IsNullOrWhiteSpace(context.NameNamespace) && candidate.VerifiedAliases.Any(alias =>
                !string.IsNullOrWhiteSpace(alias.SourceId) &&
                string.Equals(alias.Namespace, context.NameNamespace, StringComparison.Ordinal) && ProofName(alias.Surface, mention.Name, policy));
            if (candidateKind != IdentitySubjectKind.Unknown && (pairs.Count > 0 || uniqueName || backedAlias))
            {
                confirmed.Add(id);
                reasons[id] = pairs.Count > 0 ? "verified_key_equal" : uniqueName ? "unique_name_in_namespace" : "verified_alias";
            }
            else if (exactName || entity.Aliases.Any(alias => SameName(alias, mention.Name)) ||
                candidate.VerifiedAliases.Any(alias => SameName(alias.Surface, mention.Name)) ||
                candidate.Signals.FuzzySimilarity >= policy.FuzzyReviewThreshold ||
                candidate.Signals.SemanticSimilarity >= policy.SemanticReviewThreshold ||
                candidate.Signals.ContextSimilarity >= policy.ContextReviewThreshold)
            {
                review.Add(id);
                reasons[id] = "candidate_for_review";
            }
            else if (candidate.Signals.ContextRooted && policy.ReviewContextRootedCandidates)
            {
                // Nominated only by turn proximity: a quoted "different" verdict may withdraw it (no name to review).
                review.Add(id);
                reasons[id] = ContextRootedReview;
            }
        }

        // Repeated IDs with inconsistent records cannot smuggle a confirmation past a veto.
        confirmed.ExceptWith(rejected);
        review.ExceptWith(rejected);
        incomplete.ExceptWith(rejected);
        var rejectedIds = rejected.Order(StringComparer.Ordinal).ToArray();
        if (incomplete.Count > 0)
        {
            incomplete.UnionWith(confirmed);
            incomplete.UnionWith(review);
            foreach (var id in incomplete) reasons[id] = "candidate_evidence_incomplete";
            return new(IdentityDecisionKind.Pending, null, incomplete.Order(StringComparer.Ordinal).ToArray(), rejectedIds, false, reasons);
        }
        if (confirmed.Count == 1)
            return new(IdentityDecisionKind.Confirmed, confirmed.Single(), [], rejectedIds, false, reasons);
        if (confirmed.Count > 1)
            return new(IdentityDecisionKind.Pending, null, confirmed.Order(StringComparer.Ordinal).ToArray(), rejectedIds, false, reasons);
        return review.Count > 0
            ? new(IdentityDecisionKind.Pending, null, review.Order(StringComparer.Ordinal).ToArray(), rejectedIds, !literal, reasons)
            : new(IdentityDecisionKind.New, null, [], rejectedIds, false, reasons);
    }

    private static bool Complete(VerifiedIdentityKey key, EntityIdentityPolicy policy) =>
        policy.IdentityProperties.Count > 0 && !string.IsNullOrWhiteSpace(key.Namespace) &&
        !string.IsNullOrWhiteSpace(key.Authority) && !string.IsNullOrWhiteSpace(key.SourceId) &&
        policy.IdentityProperties.All(property => key.Values.Any(pair =>
            string.Equals(pair.Key, property, StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(pair.Value)));

    private static string Component(VerifiedIdentityKey key, string property) => key.Values.First(pair =>
        string.Equals(pair.Key, property, StringComparison.Ordinal)).Value;

    internal static IReadOnlyList<VerifiedIdentityKey> CompleteKeys(
        IReadOnlyList<VerifiedIdentityKey> keys, EntityIdentityPolicy policy) => keys.Where(key => Complete(key, policy)).ToArray();

    internal static bool IsCalendarLiteral(ExtractedEntity mention, EntityIdentityResolutionContext context,
        EntityIdentityPolicy policy, out CalendarValues.CalendarValue? calendar)
    {
        calendar = null;
        var kind = MentionKind(mention, context, policy);
        if (kind == IdentitySubjectKind.Reference) return false;
        var type = kind == IdentitySubjectKind.CalendarDate ? "DATE" : kind == IdentitySubjectKind.CalendarInterval ? "DATE_RANGE" : mention.Type;
        var literal = CalendarValues.TryRead(mention.Name, type, out calendar);
        if (calendar is not null && (kind == IdentitySubjectKind.CalendarDate && calendar.End is not null ||
            kind == IdentitySubjectKind.CalendarInterval && calendar.End is null)) calendar = null;
        return literal;
    }

    private static bool BackedKind(VerifiedIdentityKind? kind) => kind is not null &&
        kind.Kind is IdentitySubjectKind.Reference or IdentitySubjectKind.CalendarDate or IdentitySubjectKind.CalendarInterval &&
        !string.IsNullOrWhiteSpace(kind.Authority) && !string.IsNullOrWhiteSpace(kind.SourceId);

    private static IdentitySubjectKind DeclaredKind(string? type) =>
        string.Equals(type, "DATE_RANGE", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "CALENDAR_INTERVAL", StringComparison.OrdinalIgnoreCase)
            ? IdentitySubjectKind.CalendarInterval : CalendarValues.IsExplicitTemporalType(type) ? IdentitySubjectKind.CalendarDate : IdentitySubjectKind.Unknown;

    private static IdentitySubjectKind MentionKind(ExtractedEntity mention, EntityIdentityResolutionContext context, EntityIdentityPolicy policy)
    {
        if (BackedKind(context.VerifiedKind)) return context.VerifiedKind!.Kind;
        var declared = DeclaredKind(mention.Type);
        if (declared != IdentitySubjectKind.Unknown) return declared;
        if (context.VerifiedKeys.Any(key => Complete(key, policy))) return IdentitySubjectKind.Reference;
        return CalendarValues.TryRead(mention.Name, mention.Type, out _) ? IdentitySubjectKind.Unknown : IdentitySubjectKind.Reference;
    }

    private static IdentitySubjectKind CandidateKind(IdentityCandidate candidate)
    {
        if (BackedKind(candidate.VerifiedKind)) return candidate.VerifiedKind!.Kind;
        var declared = DeclaredKind(candidate.Entity.Type);
        if (declared != IdentitySubjectKind.Unknown) return declared;
        if (candidate.VerifiedKeys.Any(key => !string.IsNullOrWhiteSpace(key.Namespace) &&
            !string.IsNullOrWhiteSpace(key.Authority) && !string.IsNullOrWhiteSpace(key.SourceId) && key.Values.Count > 0)) return IdentitySubjectKind.Reference;
        return CalendarValues.TryRead(candidate.Entity.Name, candidate.Entity.Type, out _) ? IdentitySubjectKind.Unknown : IdentitySubjectKind.Reference;
    }

    private static bool CalendarMatches(IdentityCandidate candidate, IdentitySubjectKind kind, CalendarValues.CalendarValue calendar)
    {
        var type = kind == IdentitySubjectKind.CalendarDate ? "DATE" : kind == IdentitySubjectKind.CalendarInterval ? "DATE_RANGE" : candidate.Entity.Type;
        if (!CalendarValues.TryRead(candidate.Entity.Name, type, out var value) || value is null ||
            kind == IdentitySubjectKind.CalendarDate && value.End is not null ||
            kind == IdentitySubjectKind.CalendarInterval && value.End is null) return false;
        return CalendarValues.HasValue(candidate.Entity with { Type = type }, calendar);
    }

    private static bool CompatibleType(string mention, string candidate, EntityIdentityPolicy policy) =>
        string.Equals(mention, candidate, StringComparison.OrdinalIgnoreCase) ||
        policy.CompatibleTypes.Any(type => string.Equals(type, candidate, StringComparison.OrdinalIgnoreCase));

    private static bool ProofName(string left, string right, EntityIdentityPolicy policy) =>
        string.Equals(left, right, policy.IdentityNameComparison);

    private static bool ValidSignals(IdentityCandidateSignals signals) =>
        InRange(signals.FuzzySimilarity, 0, 1) && InRange(signals.SemanticSimilarity, -1, 1) && InRange(signals.ContextSimilarity, 0, 1);

    private static bool InRange(double? value, double min, double max) =>
        !value.HasValue || double.IsFinite(value.Value) && value >= min && value <= max;

    private static bool SameName(string left, string right) =>
        string.Equals(string.Join(" ", left.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
            string.Join(" ", right.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);

    private static bool Visible(Entity entity, MemoryScope? scope)
    {
        if (scope is null || !scope.HasOwnerFilter || SharedScopes.IsSharedOnly(scope)) return entity.OwnerId is null;
        return string.Equals(entity.OwnerId, scope.OwnerId, StringComparison.Ordinal) || scope.IncludeShared && entity.OwnerId is null;
    }
}

internal sealed record IdentityPolicyResult(IdentityDecisionKind Decision, string? ConfirmedEntityId,
    IReadOnlyList<string> PendingEntityIds, IReadOnlyList<string> RejectedEntityIds, bool CanAdjudicate,
    IReadOnlyDictionary<string, string> Reasons);
