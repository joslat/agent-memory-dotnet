using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// ILC-08: evidence-based identity re-checks a store-aware writer's stored-entity reference. Reproduces the full
/// Bariloche run: the writer named "the cutover" after the stored go-live it was shown, identity re-decided with the
/// current sentence only and kept a separate Pending event, so the go-live's place and presence were unreachable.
/// </summary>
public sealed class IdentityWriterReferenceTests
{
    private const string EventName = "Acme Logistics billing system go-live";
    private const string CurrentText = "The cutover happens on-site at Acme's office in Cordoba, and I have to be there in person to run it.";
    private const string Quote = "The cutover happens on-site";

    private readonly IIdentityAdjudicator _model = Substitute.For<IIdentityAdjudicator>();
    private int _modelCalls;

    public IdentityWriterReferenceTests()
    {
        _model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _modelCalls++;
            return "{\"decision\":\"uncertain\",\"candidateId\":null,\"evidence\":[]}";
        });
    }

    private static IdentityCandidate Candidate(string id, string name = EventName, string type = "EVENT") => new()
    {
        Entity = new() { EntityId = id, Name = name, Type = type, OwnerId = "bruna", Confidence = 1,
            SourceMessageIds = ["prior-" + id], CreatedAtUtc = DateTimeOffset.UnixEpoch },
    };

    private async Task<IdentityDecision> Plan(ExtractedEntityReference? reference, IdentityCandidate[] found,
        bool allowContextualConfirmation = true, bool withModel = true, string type = "EVENT", string text = CurrentText,
        IIdentityDecisionRepository? store = null)
    {
        var candidates = Substitute.For<IIdentityCandidateProvider>();
        candidates.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true });
        candidates.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true, Candidates = found });
        candidates.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        store ??= Substitute.For<IIdentityDecisionRepository>();
        store.IsEnabled.Returns(true); store.SupportsAtomicIngestion.Returns(true);
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());
        var ids = Substitute.For<IIdGenerator>(); ids.GenerateId().Returns("temporary");
        var options = new IdentityResolutionOptions { Enabled = true, MaxCandidates = 4, MaxAdjudicationsPerIngestion = withModel ? 2 : 0 };
        options.Policies[type] = new() { AllowContextualConfirmation = allowContextualConfirmation };
        var ingestion = new IdentityIngestion(IdentityConfiguration.Capture(options), TimeProvider.System,
            candidates, store, embeddings, ids, adjudicator: withModel ? _model : null);
        var plan = await ingestion.PlanAsync(new() { Name = EventName, Type = type, Confidence = 1, Reference = reference },
            new() { Targets = [new() { MessageId = "current", Content = text, Role = "user", SessionId = "s",
                ConversationId = "c", TimestampUtc = DateTimeOffset.UnixEpoch }] },
            MemoryScope.For("bruna", false), default);
        return plan.Decision;
    }

    [Fact]
    public async Task A_supported_writer_reference_confirms_when_the_type_policy_allows_contextual_confirmation()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("event-a");
        decision.Diagnostics.Should().Contain("writer_reference_confirmed");
        decision.ContextualEvidence!.Verdict.Should().Be(IdentityAdjudicationVerdict.Same);
        decision.ContextualEvidence.Citations.Should().ContainSingle(c => c.SourceId == "current" && c.Quote == Quote);
        decision.Observation.SourceMessageIds.Should().Equal("current");
        decision.Observation.Name.Should().Be(EventName);
        _modelCalls.Should().Be(0, "the writer's reference already carries the judgment; no second paid call");
    }

    [Fact]
    public async Task A_writer_reference_works_without_any_adjudicator()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a")], withModel: false);
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("event-a");
    }

    [Fact]
    public async Task Without_contextual_confirmation_the_reference_is_kept_for_review_and_no_model_is_called()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a")], allowContextualConfirmation: false);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.PendingEntityIds.Should().Equal("event-a");
        decision.Diagnostics.Should().Contain("writer_reference_requires_review");
        decision.ContextualEvidence!.CandidateId.Should().Be("event-a");
        _modelCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_reference_to_an_entity_that_is_not_a_candidate_falls_back_to_the_adjudicator()
    {
        var decision = await Plan(new() { EntityId = "elsewhere", Quote = Quote }, [Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().Contain("writer_reference_not_candidate").And.Contain("contextual_uncertain");
        _modelCalls.Should().Be(1);
    }

    [Fact]
    public async Task Two_identically_named_candidates_make_the_reference_ambiguous()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a"), Candidate("event-b")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.ConfirmedEntityId.Should().BeNull();
        decision.Diagnostics.Should().Contain("writer_reference_ambiguous");
        _modelCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("event-a")]
    [InlineData("event-b")]
    public async Task Reversed_identical_candidates_stay_ambiguous_for_either_reference(string referenced)
    {
        // ILCS CROSS-02 candidate-order-reversed: the order a backend returns identical candidates in cannot pick a winner.
        var decision = await Plan(new() { EntityId = referenced, Quote = Quote }, [Candidate("event-b"), Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.ConfirmedEntityId.Should().BeNull();
        decision.PendingEntityIds.Should().BeEquivalentTo(["event-a", "event-b"]);
        decision.Diagnostics.Should().Contain("writer_reference_ambiguous");
    }

    [Fact]
    public async Task A_quote_absent_from_the_current_message_cannot_support_the_reference()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = "the go-live on December 11" }, [Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().Contain("writer_reference_unsupported_quote");
    }

    [Fact]
    public async Task A_vetoed_candidate_cannot_be_confirmed_by_a_reference()
    {
        var decision = await Plan(new() { EntityId = "event-a", Quote = Quote }, [Candidate("event-a", type: "PERSON")]);
        decision.Status.Should().NotBe(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().BeNull();
        decision.Diagnostics.Should().Contain("writer_reference_not_candidate");
    }

    [Fact]
    public async Task A_reference_to_an_observation_already_confirmed_elsewhere_resolves_to_its_identity()
    {
        // Every observation is an Entity node; the writer can be shown one that a decision already confirmed into the
        // event. It is no candidate itself, so the reference is resolved through that decision (owner-scoped).
        var store = Substitute.For<IIdentityDecisionRepository>();
        store.GetAsync("identity-decision-k1", "bruna", Arg.Any<CancellationToken>()).Returns(new IdentityDecision
        {
            DecisionId = "identity-decision-k1", ObservationKey = "k1", Status = IdentityDecisionKind.Confirmed, ConfirmedEntityId = "event-a",
            Policy = new(), Observation = new() { EntityId = "identity-observation-k1", Name = EventName, Type = "EVENT", OwnerId = "bruna",
                Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch },
            Context = new(), CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch,
        });
        var decision = await Plan(new() { EntityId = "identity-observation-k1", Quote = Quote }, [Candidate("event-a")], store: store);
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("event-a");
        decision.Diagnostics.Should().Contain("writer_reference_resolved").And.Contain("writer_reference_confirmed");
        _modelCalls.Should().Be(0);
    }

    [Fact]
    public async Task An_unresolvable_observation_reference_still_falls_back()
    {
        var store = Substitute.For<IIdentityDecisionRepository>();
        store.GetAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns((IdentityDecision?)null);
        var decision = await Plan(new() { EntityId = "identity-observation-unknown", Quote = Quote }, [Candidate("event-a")], store: store);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().Contain("writer_reference_not_candidate");
        _modelCalls.Should().Be(1);
    }

    [Fact]
    public async Task No_reference_leaves_the_existing_decision_and_diagnostics_unchanged()
    {
        var decision = await Plan(null, [Candidate("event-a")]);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().NotContain(d => d.StartsWith("writer_reference", StringComparison.Ordinal));
        _modelCalls.Should().Be(1);
    }
}
