using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// ER-EXPLAIN (R23, after Senzing's match keys and AWS Entity Resolution's rule numbers): every identity decision reads as
/// one short, stable line of what decided it and what held it back, so decisions can be counted by rule ("which rule
/// gives our wrong merges?") and explained in a review. Computed from what the decision stores; nothing is decided by it.
/// </summary>
public sealed class IdentityMatchKeyTests
{
    private static IdentityDecision Decision(IdentityDecisionKind status, params string[] diagnostics) => new()
    {
        DecisionId = "d", ObservationKey = "k",
        Observation = new Entity { EntityId = "e", Name = "Esi", Type = "PERSON", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch },
        Policy = new EntityIdentityPolicy(), Status = status,
        ConfirmedEntityId = status == IdentityDecisionKind.Confirmed ? "t" : null,
        PendingEntityIds = status == IdentityDecisionKind.Pending ? ["t", "u"] : [],
        Diagnostics = diagnostics, CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch,
    };

    [Theory]
    [InlineData("same by writer-reference", "writer_reference_confirmed")]
    [InlineData("same by writer-reference-implied", "writer_reference_confirmed", "writer_reference_implied")]
    [InlineData("same by adjudicator", "contextual_candidate_sources_hydrated", "contextual_confirmed")]
    [InlineData("same by in-batch", "identity_confirmed_in_batch")]
    [InlineData("same by verified-key", "verified_key_equal")]
    [InlineData("same by rule")]
    public void A_confirmed_decision_names_the_rule_that_joined_it(string key, params string[] diagnostics) =>
        IdentityMatchKey.For(Decision(IdentityDecisionKind.Confirmed, diagnostics)).Should().Be(key);

    [Theory]
    [InlineData("possibly-same: ambiguous-name, uncertain", "writer_reference_ambiguous", "contextual_uncertain")]
    [InlineData("possibly-same: type-review-only", "contextual_same_requires_review")]
    [InlineData("possibly-same: owner-conflict", "contextual_same_owner_conflict")]
    [InlineData("possibly-same: shared-candidate", "contextual_same_shared_requires_review")]
    [InlineData("possibly-same: relation-conflict", "writer_reference_relation_conflict")]
    [InlineData("possibly-same: no-second-opinion", "contextual_timeout")]
    [InlineData("possibly-same: review")]
    public void A_pending_decision_names_what_held_it_back(string key, params string[] diagnostics) =>
        IdentityMatchKey.For(Decision(IdentityDecisionKind.Pending, diagnostics)).Should().Be(key);

    [Theory]
    [InlineData("new: different", "contextual_different")]
    [InlineData("new: different-from-all", "contextual_different_from_all")]
    [InlineData("new: no-candidate")]
    public void A_new_decision_names_why_it_stands_alone(string key, params string[] diagnostics) =>
        IdentityMatchKey.For(Decision(IdentityDecisionKind.New, diagnostics)).Should().Be(key);

    [Fact]
    public void A_reviewed_decision_says_a_person_decided_it()
    {
        var reviewed = Decision(IdentityDecisionKind.Confirmed, "writer_reference_ambiguous") with
        {
            LatestReview = new IdentityReviewEntry
            {
                OperationId = "op", RequestHash = "h", Action = IdentityReviewAction.Confirm, CandidateEntityId = "t",
                Actor = "owner", Reason = "same person", PreviousVersion = 1, Version = 2, PerformedAtUtc = DateTimeOffset.UnixEpoch,
            }
        };

        IdentityMatchKey.For(reviewed).Should().Be("same by review");
    }

    [Fact]
    public void A_stored_row_gives_the_same_key_from_its_parts()
    {
        var decision = Decision(IdentityDecisionKind.Pending, "writer_reference_ambiguous", "contextual_uncertain");

        IdentityMatchKey.For(decision.Status, review: null, decision.Diagnostics).Should().Be(IdentityMatchKey.For(decision));
        IdentityMatchKey.For(IdentityDecisionKind.New, IdentityReviewAction.Reject, diagnostics: null).Should().Be("new: rejected-on-review");
    }

    [Fact]
    public void Held_reasons_keep_one_order_and_no_repeats()
    {
        var key = IdentityMatchKey.For(Decision(IdentityDecisionKind.Pending,
            "contextual_uncertain", "writer_reference_ambiguous", "contextual_uncertain", "identity_revalidated_without_confirmation"));

        key.Should().Be("possibly-same: ambiguous-name, uncertain");
    }
}
