using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityPlannerTests
{
    private readonly IIdentityCandidateProvider _provider = Substitute.For<IIdentityCandidateProvider>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IIdGenerator _ids = Substitute.For<IIdGenerator>();
    private static readonly MemoryScope Scope = MemoryScope.For("owner", includeShared: false);
    private readonly IdentityResolutionOptions _options = new() { Enabled = true };

    public IdentityPlannerTests()
    {
        _provider.Capabilities.Returns(new IdentityCandidateCapabilities
        { BoundedReads = true, VerifiedKeyLookup = true, CalendarValueLookup = true });
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        _provider.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet());
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 1, 0 });
        _ids.GenerateId().Returns("new-source");
        _options.Policies["PERSON"] = new EntityIdentityPolicy { IdentityProperties = ["employee_id"] };
    }

    private IdentityPlanner Planner() => new(_provider, _embeddings, _ids, TimeProvider.System,
        IdentityConfiguration.Capture(_options));
    private static ExtractedEntity Mention(string name = "Alex", string type = "PERSON") => new() { Name = name, Type = type };
    private static VerifiedIdentityKey Key(string id) => new() { Namespace = "hr", Authority = "host",
        SourceId = "hr-"+id, Values = new Dictionary<string, string> { ["employee_id"] = id } };
    private static IdentityCandidate Candidate(string id, string name = "Alex", VerifiedIdentityKey? key = null) => new()
    {
        Entity = new Entity { EntityId = id, Name = name, Type = "PERSON", OwnerId = "owner",
            Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch }, VerifiedKeys = key is null ? [] : [key]
    };
    private static EntityIdentityResolutionContext Context(params VerifiedIdentityKey[] keys) => new()
    { SourceMessageIds = ["message"], VerifiedKeys = keys };

    [Fact]
    public void An_enabled_backend_must_guarantee_bounded_reads()
    {
        _provider.Capabilities.Returns(new IdentityCandidateCapabilities());
        var create = () => Planner();
        create.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void Configured_keys_require_a_real_exact_lookup_capability()
    {
        _provider.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true });
        var create = () => Planner();
        create.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task Disabled_identity_never_reads_backend_capabilities_or_issues_queries()
    {
        _options.Enabled = false;
        _options.MaxCandidates = -1;
        var planner = Planner();
        var call = () => planner.PlanAsync(Mention(), Context(), Scope);
        await call.Should().ThrowAsync<InvalidOperationException>();
        _ = _provider.DidNotReceive().Capabilities;
        await _provider.DidNotReceive().FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>());
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_verified_key_returns_the_existing_entity_without_embeddings_or_approximation()
    {
        var candidate = Candidate("existing", "Alexandra", Key("42"));
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [candidate], ExactIdentityLookupComplete = true });
        var plan = await Planner().PlanAsync(Mention(), Context(Key("42")), Scope);
        plan.Result.Decision.Should().Be(IdentityDecisionKind.Confirmed);
        plan.Entity.Should().BeSameAs(candidate.Entity);
        plan.Created.Should().BeFalse();
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _provider.DidNotReceive().FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_exact_name_without_identity_evidence_preserves_a_separate_node()
    {
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [Candidate("existing")], ExactIdentityLookupComplete = true });
        var plan = await Planner().PlanAsync(Mention(), Context(), Scope);
        plan.Result.Decision.Should().Be(IdentityDecisionKind.Pending);
        plan.Entity.EntityId.Should().Be("new-source");
        plan.Entity.Name.Should().Be("Alex");
        plan.Entity.OwnerId.Should().Be("owner");
    }

    [Fact]
    public async Task Incomplete_exact_lookup_does_not_authorise_a_visible_key_match()
    {
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [Candidate("existing", key: Key("42"))], ExactIdentityLookupComplete = false });
        var plan = await Planner().PlanAsync(Mention(), Context(Key("42")), Scope);
        plan.Result.Decision.Should().Be(IdentityDecisionKind.Pending);
        plan.Result.CanAdjudicate.Should().BeFalse();
        plan.Created.Should().BeTrue();
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_provider_cannot_hide_an_unbounded_read_behind_client_side_truncation()
    {
        _options.MaxCandidates = 1;
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [Candidate("a"), Candidate("b")], ExactIdentityLookupComplete = true });
        var act = () => Planner().PlanAsync(Mention(), Context(), Scope);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Approximation_receives_only_the_remaining_candidate_budget()
    {
        _options.MaxCandidates = 3;
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [Candidate("a"), Candidate("b")], ExactIdentityLookupComplete = true });
        await Planner().PlanAsync(Mention(), Context(), Scope);
        await _provider.Received(1).FindApproximateCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => q.Limit == 1 && q.OwnerId == "owner" && !q.IncludeShared),
            Arg.Any<float[]?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Discarded_out_of_scope_rows_still_consume_the_backend_read_budget()
    {
        _options.MaxCandidates = 1;
        var foreign = Candidate("private") with { Entity = Candidate("private").Entity with { OwnerId = "other" } };
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [foreign], ExactIdentityLookupComplete = true });
        var plan = await Planner().PlanAsync(Mention(), Context(), Scope);
        plan.Candidates.Should().BeEmpty();
        await _provider.DidNotReceive().FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>());
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Missing_calendar_precision_needs_neither_queries_nor_name_embeddings()
    {
        var plan = await Planner().PlanAsync(Mention("December 12", "OBJECT"), Context(), Scope);
        plan.Entity.Name.Should().Be("December 12");
        plan.Result.Decision.Should().Be(IdentityDecisionKind.New);
        await _provider.DidNotReceive().FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>());
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Complete_calendar_literals_use_the_value_lookup_without_approximation()
    {
        await Planner().PlanAsync(Mention("12 December 2026", "OBJECT"), Context(), Scope);
        await _provider.Received(1).FindExactCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => q.CalendarKey == "gregorian:2026-12-12"), Arg.Any<CancellationToken>());
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("December 9–13, 2026")]
    [InlineData("2026-12-09/2026-12-13")]
    public async Task A_complete_interval_key_contains_both_endpoints_and_cannot_be_a_single_date_key(string mention)
    {
        await Planner().PlanAsync(Mention(mention, "OBJECT"), Context(), Scope);
        await _provider.Received(1).FindExactCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => q.CalendarKey == "gregorian-range:2026-12-09/2026-12-13"), Arg.Any<CancellationToken>());
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_incomplete_interval_never_enters_the_name_candidate_route()
    {
        var plan = await Planner().PlanAsync(Mention("December 9–13", "OBJECT"), Context(), Scope);
        plan.Entity.Name.Should().Be("December 9–13");
        await _provider.DidNotReceive().FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>());
        await _embeddings.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_ownerless_write_never_uses_global_private_candidates()
    {
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [Candidate("private", key: Key("42"))], ExactIdentityLookupComplete = true });
        var plan = await Planner().PlanAsync(Mention(), Context(Key("42")), scope: null);
        plan.Entity.OwnerId.Should().BeNull();
        plan.Result.ConfirmedEntityId.Should().BeNull();
        plan.Result.PendingEntityIds.Should().BeEmpty();
        plan.Result.RejectedEntityIds.Should().BeEmpty();
    }

    [Fact]
    public async Task Caller_mutation_while_the_backend_is_pending_cannot_change_the_identity_key()
    {
        var values = new Dictionary<string, string> { ["employee_id"] = "42" };
        var key = Key("42") with { Values = values };
        var response = new TaskCompletionSource<IdentityCandidateSet>(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>()).Returns(response.Task);
        var task = Planner().PlanAsync(Mention(), Context(key), Scope);
        values["employee_id"] = "43";
        response.SetResult(new IdentityCandidateSet { Candidates = [Candidate("existing", key: Key("42"))], ExactIdentityLookupComplete = true });
        var plan = await task;
        plan.Result.ConfirmedEntityId.Should().Be("existing");
    }

    [Fact]
    public async Task An_empty_embedding_does_not_drop_the_observation()
    {
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());
        var plan = await Planner().PlanAsync(Mention(), Context(), Scope);
        plan.Created.Should().BeTrue();
        plan.Entity.Name.Should().Be("Alex");
        await _provider.Received(1).FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_after_a_backend_response_cannot_return_a_resolution()
    {
        using var cancellation = new CancellationTokenSource();
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => { cancellation.Cancel(); return Task.FromResult(new IdentityCandidateSet { ExactIdentityLookupComplete = true }); });
        var act = () => Planner().PlanAsync(Mention(), Context(), Scope, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task The_backend_receives_the_configured_composite_key_shape()
    {
        await Planner().PlanAsync(Mention(), Context(Key("42")), Scope);
        await _provider.Received(1).FindExactCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => q.IdentityProperties.SequenceEqual(new[] { "employee_id" }) && q.VerifiedKeys.Count == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unconfigured_keys_are_not_sent_to_an_exact_lookup()
    {
        _options.Policies.Clear();
        await Planner().PlanAsync(Mention(), Context(Key("42")), Scope);
        await _provider.Received(1).FindExactCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => q.VerifiedKeys.Count == 0 && q.IdentityProperties.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ontology_compatible_types_and_disabled_fuzzy_route_are_explicit_backend_query_data()
    {
        _options.Policies["PERSON"] = new() { CompatibleTypes = ["EMPLOYEE"] };
        var planner = new IdentityPlanner(_provider, _embeddings, _ids, TimeProvider.System,
            IdentityConfiguration.Capture(_options), useFuzzyCandidates: false);
        await planner.PlanAsync(Mention(), Context(), Scope);
        await _provider.Received(1).FindExactCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => q.CompatibleTypes.SequenceEqual(new[] { "EMPLOYEE" }) && !q.UseFuzzyCandidates), Arg.Any<CancellationToken>());
        await _provider.Received(1).FindApproximateCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => !q.UseFuzzyCandidates), Arg.Any<float[]?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_backend_cannot_claim_an_unsupported_case_insensitive_exact_identity_lookup()
    {
        _options.Policies["PERSON"] = new() { IdentityNameComparison = StringComparison.OrdinalIgnoreCase };
        var create = () => Planner();
        create.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task A_supported_authority_name_comparison_is_sent_to_the_backend()
    {
        _options.Policies["PERSON"] = new() { IdentityNameComparison = StringComparison.OrdinalIgnoreCase };
        _provider.Capabilities.Returns(new IdentityCandidateCapabilities
        { BoundedReads = true, CalendarValueLookup = true, CaseInsensitiveIdentityNameLookup = true });
        await Planner().PlanAsync(Mention(), Context(), Scope);
        await _provider.Received(1).FindExactCandidatesAsync(
            Arg.Is<IdentityCandidateQuery>(q => q.IdentityNameComparison == StringComparison.OrdinalIgnoreCase), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Exact_and_approximate_routes_share_one_candidate_snapshot_per_entity()
    {
        var candidate = Candidate("same");
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [candidate], ExactIdentityLookupComplete = true });
        _provider.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [candidate] });
        var plan = await Planner().PlanAsync(Mention(), Context(), Scope);
        plan.Candidates.Should().ContainSingle().Which.Entity.EntityId.Should().Be("same");
        plan.Result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Fact]
    public async Task Approximate_overlap_does_not_replace_the_exact_authority_snapshot()
    {
        _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [Candidate("same")], ExactIdentityLookupComplete = true });
        _provider.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { Candidates = [Candidate("same", key: Key("42"))] });
        var plan = await Planner().PlanAsync(Mention(), Context(Key("42")), Scope);
        plan.Candidates.Should().ContainSingle().Which.VerifiedKeys.Should().BeEmpty();
        plan.Result.Decision.Should().Be(IdentityDecisionKind.Pending);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Duplicates_inside_one_provider_route_are_a_contract_violation(bool exact)
    {
        var set = new IdentityCandidateSet { Candidates = [Candidate("same"), Candidate("same")], ExactIdentityLookupComplete = true };
        if (exact) _provider.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>()).Returns(set);
        else _provider.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>()).Returns(set);
        var action = () => Planner().PlanAsync(Mention(), Context(), Scope);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }
}
