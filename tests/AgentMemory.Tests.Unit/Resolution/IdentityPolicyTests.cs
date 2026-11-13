using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityPolicyTests
{
    private static readonly EntityIdentityPolicy Policy = new() { IdentityProperties = ["employee_id"] };
    private static readonly MemoryScope Scope = MemoryScope.For("owner", includeShared: false);

    private static VerifiedIdentityKey Key(string value, string ns = "acme-hr") => new()
    {
        Namespace = ns, Authority = "host-hr", SourceId = "hr-record-" + value,
        Values = new Dictionary<string, string> { ["employee_id"] = value }
    };

    private static IdentityCandidate Candidate(string id, string name = "Alex Rivera", VerifiedIdentityKey? key = null,
        double? fuzzy = null, double? semantic = null, string? owner = "owner") => new()
    {
        Entity = new Entity { EntityId = id, Name = name, Type = "PERSON", OwnerId = owner,
            Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch },
        VerifiedKeys = key is null ? [] : [key],
        Signals = new IdentityCandidateSignals { FuzzySimilarity = fuzzy, SemanticSimilarity = semantic }
    };

    private static EntityIdentityResolutionContext Context(params VerifiedIdentityKey[] keys) => new()
    {
        SourceMessageIds = ["message"], VerifiedKeys = keys
    };

    private static IdentityPolicyResult Decide(IReadOnlyList<IdentityCandidate> candidates,
        EntityIdentityResolutionContext? context = null, EntityIdentityPolicy? policy = null,
        string mention = "Alex Rivera") => IdentityPolicy.Evaluate(
            new ExtractedEntity { Name = mention, Type = "PERSON" }, candidates,
            context ?? Context(), policy ?? Policy, Scope);

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.85, 0.8)]
    [InlineData(1.0, 1.0)]
    public void Similarity_never_confirms_identity(double fuzzy, double semantic)
    {
        var result = Decide([Candidate("a", fuzzy: fuzzy, semantic: semantic)]);
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
        result.ConfirmedEntityId.Should().BeNull();
    }

    [Fact]
    public void A_verified_complete_key_can_confirm_despite_a_different_display_name()
    {
        var result = Decide([Candidate("a", "A. Rivera", Key("42"))], Context(Key("42")));
        result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
        result.ConfirmedEntityId.Should().Be("a");
        result.CanAdjudicate.Should().BeFalse();
    }

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(0.85, 0.8)]
    [InlineData(1.0, 1.0)]
    public void A_verified_key_conflict_vetoes_even_identical_names_and_vectors(double fuzzy, double semantic)
    {
        var result = Decide([Candidate("a", key: Key("43"), fuzzy: fuzzy, semantic: semantic)], Context(Key("42")));
        result.Decision.Should().Be(IdentityDecisionKind.New);
        result.RejectedEntityIds.Should().Contain("a");
        result.CanAdjudicate.Should().BeFalse();
    }

    [Fact]
    public void Keys_in_different_namespaces_do_not_confirm_or_contradict_each_other()
    {
        var result = Decide([Candidate("a", key: Key("42", "other-company"))], Context(Key("42")));
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
        result.RejectedEntityIds.Should().BeEmpty();
    }

    [Fact]
    public void A_missing_composite_component_cannot_confirm()
    {
        var policy = new EntityIdentityPolicy { IdentityProperties = ["company_id", "employee_id"] };
        var result = Decide([Candidate("a", key: Key("42"))], Context(Key("42")), policy);
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void Extractor_attributes_are_not_verified_identity_keys()
    {
        var candidate = Candidate("a") with
        {
            Entity = Candidate("a").Entity with { Attributes = new Dictionary<string, object> { ["employee_id"] = "42" } }
        };
        var result = Decide([candidate], Context(Key("42")));
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void Duplicate_verified_keys_are_an_ambiguity_not_a_tie_breaker()
    {
        var result = Decide([Candidate("b", key: Key("42")), Candidate("a", key: Key("42"))], Context(Key("42")));
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
        result.PendingEntityIds.Should().Equal("a", "b");
        result.CanAdjudicate.Should().BeFalse("a uniqueness violation must not be hidden by a model choice");
    }

    [Fact]
    public void An_out_of_scope_candidate_cannot_be_confirmed_by_a_key()
    {
        var result = Decide([Candidate("a", key: Key("42"), owner: "another-owner")], Context(Key("42")));
        result.Decision.Should().Be(IdentityDecisionKind.New);
        result.PendingEntityIds.Should().BeEmpty();
        result.RejectedEntityIds.Should().BeEmpty("private candidate IDs must not escape through diagnostics");
        result.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void A_type_conflict_is_not_overridden_by_an_exact_name()
    {
        var candidate = Candidate("a") with { Entity = Candidate("a").Entity with { Type = "ORGANIZATION" } };
        Decide([candidate]).Decision.Should().Be(IdentityDecisionKind.New);
    }

    [Fact]
    public void A_unique_name_requires_the_verified_namespace_on_both_sides()
    {
        var policy = new EntityIdentityPolicy { UniqueNameNamespace = "directory" };
        var candidate = Candidate("a") with { NameNamespace = "directory" };
        Decide([candidate], policy: policy).Decision.Should().Be(IdentityDecisionKind.Pending);
        var result = Decide([candidate], Context() with { NameNamespace = "directory" }, policy);
        result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public void A_confirmed_alias_must_have_provenance_and_match_the_namespace()
    {
        var candidate = Candidate("a", "Alexandra Rivera") with
        {
            VerifiedAliases = [new VerifiedIdentityAlias { Surface = "Alex", Namespace = "contacts", SourceId = "source" }]
        };
        var result = Decide([candidate], Context() with { NameNamespace = "contacts" }, mention: "Alex");
        result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
        Decide([candidate], mention: "Alex").Decision.Should().NotBe(IdentityDecisionKind.Confirmed);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void Invalid_similarity_scores_are_not_admissible_evidence(double score)
    {
        var result = Decide([Candidate("a", "Someone else", fuzzy: score)]);
        result.Decision.Should().Be(IdentityDecisionKind.New);
        result.CanAdjudicate.Should().BeFalse();
    }

    [Fact]
    public void Candidate_order_does_not_choose_between_homonyms()
    {
        var a = Candidate("a");
        var b = Candidate("b");
        Decide([a, b]).PendingEntityIds.Should().Equal(Decide([b, a]).PendingEntityIds);
        Decide([a, b]).Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void Candidate_order_does_not_change_the_serialized_policy_receipt()
    {
        var a = Candidate("a");
        var b = Candidate("b");
        System.Text.Json.JsonSerializer.Serialize(Decide([a, b])).Should()
            .Be(System.Text.Json.JsonSerializer.Serialize(Decide([b, a])));
    }

    [Theory]
    [InlineData(null, null, true, true)]
    [InlineData(null, "owner", true, false)]
    [InlineData("", "owner", true, false)]
    [InlineData("owner", "owner", false, true)]
    [InlineData("owner", "other", true, false)]
    [InlineData("owner", null, false, false)]
    [InlineData("owner", null, true, true)]
    public void Identity_scope_is_rechecked_even_when_the_backend_claims_a_verified_key_match(
        string? owner, string? candidateOwner, bool includeShared, bool visible)
    {
        var scope = owner is null ? null : MemoryScope.For(owner, includeShared);
        var result = IdentityPolicy.Evaluate(new ExtractedEntity { Name = "Alex Rivera", Type = "PERSON" },
            [Candidate("private-id", key: Key("42"), owner: candidateOwner)], Context(Key("42")), Policy, scope);
        result.Decision.Should().Be(visible ? IdentityDecisionKind.Confirmed : IdentityDecisionKind.New);
        if (!visible) result.Reasons.Should().BeEmpty();
    }

    [Fact]
    public void Similarity_thresholds_are_per_type_and_only_control_review()
    {
        var candidate = Candidate("a", "Alyx Rivera", fuzzy: 0.9);
        Decide([candidate]).Decision.Should().Be(IdentityDecisionKind.Pending);
        Decide([candidate], policy: new EntityIdentityPolicy { FuzzyReviewThreshold = 0.95 })
            .Decision.Should().Be(IdentityDecisionKind.New);
    }

    [Fact]
    public void Two_correlated_name_signals_do_not_accumulate_into_confirmation()
    {
        var result = Decide([Candidate("a", "Alyx Rivera", fuzzy: 0.99, semantic: 0.99)]);
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    public static IEnumerable<object?[]> ConflictingKeySignals()
    {
        foreach (var fuzzy in new double?[] { null, 0, 0.85, 1 })
        foreach (var semantic in new double?[] { null, -1, 0.8, 1 })
        foreach (var context in new double?[] { null, 0, 1 })
        foreach (var exactName in new[] { false, true })
            yield return [fuzzy, semantic, context, exactName];
    }

    [Theory]
    [MemberData(nameof(ConflictingKeySignals))]
    public void No_combination_of_similarity_signals_can_overrule_a_key_conflict(
        double? fuzzy, double? semantic, double? contextScore, bool exactName)
    {
        var candidate = Candidate("a", exactName ? "Alex Rivera" : "A. Rivera", Key("43"), fuzzy, semantic) with
        {
            Signals = new IdentityCandidateSignals { FuzzySimilarity = fuzzy,
                SemanticSimilarity = semantic, ContextSimilarity = contextScore }
        };
        var result = Decide([candidate], Context(Key("42")),
            new EntityIdentityPolicy { IdentityProperties = ["employee_id"], AllowContextualConfirmation = true });
        result.Decision.Should().Be(IdentityDecisionKind.New);
        result.ConfirmedEntityId.Should().BeNull();
        result.CanAdjudicate.Should().BeFalse();
    }

    [Fact]
    public void A_shared_hub_and_two_name_signals_do_not_establish_identity()
    {
        var candidate = Candidate("a", "Alyx Rivera", fuzzy: 0.99, semantic: 0.99) with
        {
            Signals = new IdentityCandidateSignals { FuzzySimilarity = 0.99,
                SemanticSimilarity = 0.99, ContextSimilarity = 1 }
        };
        Decide([candidate]).Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void An_incomplete_or_unattributed_key_is_not_automatically_verified()
    {
        var key = Key("42") with { Authority = "" };
        var result = Decide([Candidate("a", key: key)], Context(Key("42")));
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void Conflicting_active_keys_on_one_candidate_veto_a_matching_key()
    {
        var candidate = Candidate("a") with { VerifiedKeys = [Key("42"), Key("43")] };
        var result = Decide([candidate], Context(Key("42")));
        result.Decision.Should().Be(IdentityDecisionKind.New);
        result.RejectedEntityIds.Should().Contain("a");
    }

    [Fact]
    public void Duplicate_candidate_rows_cannot_hide_a_veto()
    {
        var good = Candidate("a", key: Key("42"));
        var bad = Candidate("a", key: Key("43"));
        Decide([good, bad], Context(Key("42"))).Decision.Should().Be(IdentityDecisionKind.New);
        Decide([bad, good], Context(Key("42"))).Decision.Should().Be(IdentityDecisionKind.New);
    }

    [Fact]
    public void A_host_verified_reference_does_not_become_a_literal_from_its_display_name()
    {
        var candidate = Candidate("a", "2026-12-12", Key("42")) with
        { Entity = Candidate("a", "2026-12-12").Entity with { Type = "OBJECT" } };
        var result = IdentityPolicy.Evaluate(new ExtractedEntity { Name = "2026-12-12", Type = "OBJECT" },
            [candidate], Context(Key("42")), Policy, Scope);
        result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
        result.ConfirmedEntityId.Should().Be("a");
    }

    [Fact]
    public void Calendar_shaped_display_names_cannot_hide_a_verified_reference_key_conflict()
    {
        var candidate = Candidate("a", "2026-12-12", Key("43")) with
        { Entity = Candidate("a", "2026-12-12").Entity with { Type = "OBJECT" } };
        var result = IdentityPolicy.Evaluate(new ExtractedEntity { Name = "2026-12-12", Type = "OBJECT" },
            [candidate], Context(Key("42")), Policy, Scope);
        result.Decision.Should().Be(IdentityDecisionKind.New);
    }

    [Fact]
    public void A_calendar_literal_does_not_resolve_to_a_known_reference_with_the_same_name()
    {
        var candidate = Candidate("a", "2026-12-12", Key("42")) with
        { Entity = Candidate("a", "2026-12-12").Entity with { Type = "OBJECT" } };
        var result = IdentityPolicy.Evaluate(new ExtractedEntity { Name = "2026-12-12", Type = "OBJECT" },
            [candidate], Context(), Policy, Scope);
        result.Decision.Should().Be(IdentityDecisionKind.New);
    }

    [Fact]
    public void Explicit_calendar_values_cannot_be_made_equal_by_an_unrelated_host_key()
    {
        var candidate = Candidate("a", "2026-12-03", Key("42")) with
        { Entity = Candidate("a", "2026-12-03").Entity with { Type = "DATE" } };
        var result = IdentityPolicy.Evaluate(new ExtractedEntity { Name = "2026-12-12", Type = "DATE" },
            [candidate], Context(Key("42")), Policy, Scope);
        result.Decision.Should().Be(IdentityDecisionKind.New);
    }
}
