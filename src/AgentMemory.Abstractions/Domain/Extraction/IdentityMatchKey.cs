namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// ER-EXPLAIN (after Senzing's match keys and AWS Entity Resolution's rule numbers): one short, stable line of what decided
/// an identity decision and what held it back, computed from what the decision stores (<see cref="IdentityDecision.Status"/>,
/// <see cref="IdentityDecision.LatestReview"/> and <see cref="IdentityDecision.Diagnostics"/>). It explains a decision in a
/// review and lets decisions be counted by rule; nothing is decided by it.
/// </summary>
/// <remarks>
/// The grammar, kept stable: <c>same by RULE</c>, where RULE is <c>review</c>, <c>verified-key</c>,
/// <c>writer-reference</c>, <c>writer-reference-implied</c>, <c>adjudicator</c>, <c>in-batch</c> or <c>rule</c>;
/// <c>possibly-same: HELD[, HELD]</c>, where each HELD is <c>ambiguous-name</c>, <c>uncertain</c>,
/// <c>type-review-only</c>, <c>owner-conflict</c>, <c>shared-candidate</c>, <c>relation-conflict</c>,
/// <c>no-second-opinion</c> or <c>review</c>; and <c>new: WHY</c>, where WHY is <c>rejected-on-review</c>,
/// <c>revoked-on-review</c>, <c>different</c>, <c>different-from-all</c> or <c>no-candidate</c>.
/// </remarks>
public static class IdentityMatchKey
{
    private static readonly (string Diagnostic, string Held)[] HeldBy =
    [
        ("writer_reference_ambiguous", "ambiguous-name"),
        ("contextual_uncertain", "uncertain"),
        ("contextual_same_requires_review", "type-review-only"),
        ("contextual_same_owner_conflict", "owner-conflict"),
        ("contextual_same_shared_requires_review", "shared-candidate"),
        ("writer_reference_relation_conflict", "relation-conflict"),
        ("contextual_timeout", "no-second-opinion"),
        ("contextual_unavailable", "no-second-opinion"),
        ("contextual_call_budget", "no-second-opinion"),
    ];

    /// <summary>The decision's match key; see the remarks for its grammar.</summary>
    public static string For(IdentityDecision decision)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return For(decision.Status, decision.LatestReview?.Action, decision.Diagnostics);
    }

    /// <summary>
    /// The match key from a decision's stored parts: its status, its latest review's action (null when never reviewed) and
    /// its diagnostics. For rows read back from a report or a store export without the whole decision.
    /// </summary>
    public static string For(IdentityDecisionKind status, IdentityReviewAction? review, IEnumerable<string>? diagnostics)
    {
        var said = diagnostics?.ToHashSet(StringComparer.Ordinal) ?? [];
        bool Has(string diagnostic) => said.Contains(diagnostic);
        switch (status)
        {
            case IdentityDecisionKind.Confirmed:
                return "same by " + (review == IdentityReviewAction.Confirm ? "review"
                    : Has("verified_key_equal") ? "verified-key"
                    : Has("writer_reference_implied") ? "writer-reference-implied"
                    : Has("writer_reference_confirmed") ? "writer-reference"
                    : Has("contextual_confirmed") ? "adjudicator"
                    : Has("identity_confirmed_in_batch") ? "in-batch"
                    : "rule");
            case IdentityDecisionKind.Pending:
                var held = HeldBy.Where(pair => Has(pair.Diagnostic)).Select(pair => pair.Held).Distinct(StringComparer.Ordinal).ToList();
                return "possibly-same: " + (held.Count == 0 ? "review" : string.Join(", ", held));
            default:
                return "new: " + (review == IdentityReviewAction.Reject ? "rejected-on-review"
                    : review == IdentityReviewAction.Revoke ? "revoked-on-review"
                    : Has("contextual_different_from_all") ? "different-from-all"
                    : Has("contextual_different") ? "different"
                    : "no-candidate");
        }
    }
}
