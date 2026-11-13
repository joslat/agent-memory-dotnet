using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Options;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityOntologyTests
{
    private static readonly MemoryScope Scope = MemoryScope.For("owner", includeShared: false);
    private static VerifiedIdentityKind Kind(IdentitySubjectKind kind) => new()
    { Kind = kind, Authority = "host", SourceId = "kind-proof" };
    private static Entity Entity(string name = "Alex", string type = "PERSON") => new()
    { EntityId = "candidate", Name = name, Type = type, OwnerId = "owner", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };
    private static IdentityPolicyResult Decide(string name, IdentityCandidate candidate,
        EntityIdentityResolutionContext? context = null, EntityIdentityPolicy? policy = null, string type = "OBJECT") =>
        IdentityPolicy.Evaluate(new ExtractedEntity { Name = name, Type = type }, [candidate], context ?? new(), policy ?? new(), Scope);

    [Fact]
    public void A_date_shaped_object_is_protected_without_claiming_its_unknown_semantic_role_is_proved()
    {
        var result = Decide("2026-12-12", new() { Entity = Entity("2026-12-12", "OBJECT") });
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
        result.ConfirmedEntityId.Should().BeNull();
        result.CanAdjudicate.Should().BeFalse();
    }

    [Theory]
    [InlineData(IdentitySubjectKind.Reference)]
    [InlineData(IdentitySubjectKind.CalendarInterval)]
    public void A_verified_kind_conflict_is_not_compensated_by_identical_display_names(IdentitySubjectKind otherKind)
    {
        var result = Decide("2026-12-12", new() { Entity = Entity("2026-12-12", "OBJECT"), VerifiedKind = Kind(otherKind),
            Signals = new() { FuzzySimilarity = 1, SemanticSimilarity = 1, ContextSimilarity = 1 } },
            new() { VerifiedKind = Kind(IdentitySubjectKind.CalendarDate) });
        result.Decision.Should().Be(IdentityDecisionKind.New);
        result.CanAdjudicate.Should().BeFalse();
    }

    [Theory]
    [InlineData("2026-12-12", "12 December 2026", IdentitySubjectKind.CalendarDate)]
    [InlineData("2026-12-09/2026-12-13", "December 9–13, 2026", IdentitySubjectKind.CalendarInterval)]
    public void Trusted_literal_kinds_and_equal_components_can_confirm(string mention, string stored, IdentitySubjectKind kind)
    {
        var result = Decide(mention, new() { Entity = Entity(stored, "OBJECT"), VerifiedKind = Kind(kind) },
            new() { VerifiedKind = Kind(kind) });
        result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public void A_kind_marker_without_authority_and_source_does_not_establish_role()
    {
        var unbacked = Kind(IdentitySubjectKind.CalendarDate) with { Authority = "" };
        var result = Decide("2026-12-12", new() { Entity = Entity("2026-12-12", "OBJECT"), VerifiedKind = unbacked },
            new() { VerifiedKind = unbacked });
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void A_verified_reference_can_have_a_date_shaped_name_without_becoming_a_value()
    {
        var result = Decide("2026-12-12", new() { Entity = Entity("2026-12-12", "OBJECT"),
            VerifiedKind = Kind(IdentitySubjectKind.Reference), NameNamespace = "catalog" },
            new() { VerifiedKind = Kind(IdentitySubjectKind.Reference), NameNamespace = "catalog" },
            new() { UniqueNameNamespace = "catalog" });
        result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
        result.Reasons["candidate"].Should().Be("unique_name_in_namespace");
    }

    [Fact]
    public void A_backed_alias_can_identify_a_verified_reference_whose_alias_looks_like_a_date()
    {
        var result = Decide("2026-12-12", new() { Entity = Entity("Release Twelve", "OBJECT"),
            VerifiedKind = Kind(IdentitySubjectKind.Reference), VerifiedAliases =
            [new() { Surface = "2026-12-12", Namespace = "catalog", SourceId = "alias-proof" }] },
            new() { VerifiedKind = Kind(IdentitySubjectKind.Reference), NameNamespace = "catalog" });
        result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
        result.Reasons["candidate"].Should().Be("verified_alias");
    }

    [Theory]
    [InlineData("ALPHA", "alpha")]
    [InlineData(" A", "A")]
    [InlineData("A B", "A  B")]
    public void Namespace_uniqueness_does_not_silently_casefold_or_collapse_identity_whitespace(string mention, string stored)
    {
        var result = Decide(mention, new() { Entity = Entity(stored, "OBJECT"), NameNamespace = "catalog" },
            new() { NameNamespace = "catalog" }, new() { UniqueNameNamespace = "catalog" });
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void Explicit_ordinal_ignore_case_identity_comparison_only_changes_case_semantics()
    {
        var policy = new EntityIdentityPolicy { UniqueNameNamespace = "catalog", IdentityNameComparison = StringComparison.OrdinalIgnoreCase };
        var context = new EntityIdentityResolutionContext { NameNamespace = "catalog" };
        Decide("ALPHA", new() { Entity = Entity("alpha", "OBJECT"), NameNamespace = "catalog" }, context, policy)
            .Decision.Should().Be(IdentityDecisionKind.Confirmed);
        Decide("A B", new() { Entity = Entity("A  B", "OBJECT"), NameNamespace = "catalog" }, context, policy)
            .Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void Backed_aliases_use_the_identity_comparison_rather_than_a_candidate_normalizer()
    {
        var result = Decide("ALPHA", new() { Entity = Entity("Product", "OBJECT"), VerifiedAliases =
            [new() { Surface = "alpha", Namespace = "catalog", SourceId = "alias-proof" }] },
            new() { NameNamespace = "catalog" });
        result.Decision.Should().NotBe(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public void Key_component_names_are_ordinal_even_if_the_host_dictionary_has_a_different_comparer()
    {
        var left = new VerifiedIdentityKey { Namespace = "hr", Authority = "host", SourceId = "left",
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["EMPLOYEE_ID"] = "42" } };
        var right = left with { SourceId = "right", Values = new Dictionary<string, string> { ["employee_id"] = "42" } };
        var result = Decide("Alex", new() { Entity = Entity(), VerifiedKeys = [right] }, new() { VerifiedKeys = [left] },
            new() { IdentityProperties = ["employee_id"] }, type: "PERSON");
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public void An_explicit_compatible_type_can_use_a_complete_verified_key()
    {
        var key = new VerifiedIdentityKey { Namespace = "hr", Authority = "host", SourceId = "hr42",
            Values = new Dictionary<string, string> { ["employee_id"] = "42" } };
        var result = Decide("Alex", new() { Entity = Entity("Alexandra", "EMPLOYEE"), VerifiedKeys = [key] },
            new() { VerifiedKeys = [key] }, new() { IdentityProperties = ["employee_id"], CompatibleTypes = ["EMPLOYEE"] }, type: "PERSON");
        result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
    }

    [Fact]
    public void Ontology_collections_are_frozen_before_async_work()
    {
        var types = new List<string> { "EMPLOYEE" }; var context = new List<string> { "department" };
        var options = new IdentityResolutionOptions { Enabled = true };
        options.Policies["PERSON"] = new() { CompatibleTypes = types, ContextProperties = context };
        var captured = IdentityConfiguration.Capture(options); types.Clear(); context.Clear();
        captured.PolicyFor("PERSON").CompatibleTypes.Should().Equal("EMPLOYEE");
        captured.PolicyFor("PERSON").ContextProperties.Should().Equal("department");
    }

    [Fact]
    public void Culture_based_identity_name_comparison_is_not_an_admissible_ontology_rule()
    {
        var options = new IdentityResolutionOptions { Enabled = true };
        options.Policies["OBJECT"] = new() { IdentityNameComparison = StringComparison.CurrentCultureIgnoreCase };
        var capture = () => IdentityConfiguration.Capture(options);
        capture.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Bounded_but_incomplete_evidence_cannot_confirm_a_visible_matching_key()
    {
        var key = new VerifiedIdentityKey { Namespace = "hr", Authority = "host", SourceId = "hr42",
            Values = new Dictionary<string, string> { ["employee_id"] = "42" } };
        var result = Decide("Alex", new() { Entity = Entity(), VerifiedKeys = [key], EvidenceComplete = false },
            new() { VerifiedKeys = [key] }, new() { IdentityProperties = ["employee_id"] }, type: "PERSON");
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
        result.CanAdjudicate.Should().BeFalse();
    }

    [Fact]
    public void Another_incomplete_candidate_cannot_hide_a_competing_proof_behind_a_complete_match()
    {
        var key = new VerifiedIdentityKey { Namespace = "hr", Authority = "host", SourceId = "hr42",
            Values = new Dictionary<string, string> { ["employee_id"] = "42" } };
        var result = IdentityPolicy.Evaluate(new() { Name = "Alex", Type = "PERSON" },
            [new() { Entity = Entity(), VerifiedKeys = [key] },
             new() { Entity = Entity() with { EntityId = "other" }, EvidenceComplete = false }],
            new() { VerifiedKeys = [key] }, new() { IdentityProperties = ["employee_id"] }, Scope);
        result.Decision.Should().Be(IdentityDecisionKind.Pending);
        result.PendingEntityIds.Should().Equal("candidate", "other");
        result.CanAdjudicate.Should().BeFalse();
    }

    [Fact]
    public void A_visible_contradiction_still_vetoes_when_more_evidence_is_unavailable()
    {
        var left = new VerifiedIdentityKey { Namespace = "hr", Authority = "host", SourceId = "hr42",
            Values = new Dictionary<string, string> { ["employee_id"] = "42" } };
        var right = left with { Values = new Dictionary<string, string> { ["employee_id"] = "43" } };
        var result = Decide("Alex", new() { Entity = Entity(), VerifiedKeys = [right], EvidenceComplete = false },
            new() { VerifiedKeys = [left] }, new() { IdentityProperties = ["employee_id"] }, type: "PERSON");
        result.Decision.Should().Be(IdentityDecisionKind.New);
        result.RejectedEntityIds.Should().Equal("candidate");
    }
}
