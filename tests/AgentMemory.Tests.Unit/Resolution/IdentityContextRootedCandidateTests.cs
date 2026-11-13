using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>
/// ILC-13: the full Bariloche run real-03 named the cutover "Cutover" without a writer reference; no name or vector route
/// nominated the go-live, so neither the reference check nor the adjudicator could decide. An opted-in type nominates
/// same-type entities first observed in the window's read-only context turns, for review only.
/// </summary>
public sealed class IdentityContextRootedCandidateTests
{
    private const string PriorText = "We just signed with Acme Logistics. I'm leading the go-live of their new billing system on Friday, December 11.";
    private const string CurrentText = "The cutover happens on-site at Acme's office in Cordoba, and I have to be there in person to run it.";
    private readonly IIdentityCandidateProvider _candidates = Substitute.For<IIdentityCandidateProvider>();
    private readonly IIdentityAdjudicator _model = Substitute.For<IIdentityAdjudicator>();
    private readonly List<IdentityCandidateQuery> _approximate = [];
    private int _modelCalls;

    public IdentityContextRootedCandidateTests()
    {
        _candidates.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true, ContextRootedLookup = true });
        _candidates.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        _candidates.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var query = call.ArgAt<IdentityCandidateQuery>(0); _approximate.Add(query);
            return query.ContextSourceMessageIds.Contains("prior")
                ? new IdentityCandidateSet { ExactIdentityLookupComplete = true, Candidates = [new()
                  { Entity = new() { EntityId = "event-a", Name = "Acme Logistics billing system go-live", Type = TypeOf(query), OwnerId = "bruna",
                      Confidence = 1, SourceMessageIds = ["prior"], CreatedAtUtc = DateTimeOffset.UnixEpoch }, Signals = new() { ContextRooted = true } }] }
                : new IdentityCandidateSet { ExactIdentityLookupComplete = true };
        });
        _model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _modelCalls++;
            using var document = JsonDocument.Parse(call.Arg<IdentityAdjudicationRequest>().DataJson);
            var hasPrior = document.RootElement.GetProperty("sources").EnumerateArray().Any(s => s.GetProperty("SourceId").GetString() == "prior");
            return hasPrior
                ? "{\"decision\":\"same\",\"candidateId\":\"event-a\",\"evidence\":[{\"sourceId\":\"prior\",\"quote\":\"leading the go-live\"},{\"sourceId\":\"current\",\"quote\":\"The cutover happens on-site\"}]}"
                : "{\"decision\":\"uncertain\",\"candidateId\":null,\"evidence\":[]}";
        });
    }

    private static string TypeOf(IdentityCandidateQuery query) => query.Type;

    private static Message Message(string id, string text) => new()
    { MessageId = id, Content = text, Role = "user", SessionId = "s", ConversationId = "c", TimestampUtc = DateTimeOffset.UnixEpoch };

    private Task<IdentityDecisionWrite> Plan(bool optIn = true, bool withContext = true, string candidateType = "EVENT", IIdentityCandidateProvider? provider = null)
    {
        var store = Substitute.For<IIdentityDecisionRepository>();
        store.IsEnabled.Returns(true); store.SupportsAtomicIngestion.Returns(true);
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());
        var ids = Substitute.For<IIdGenerator>(); ids.GenerateId().Returns("temporary");
        var options = new IdentityResolutionOptions { Enabled = true, MaxCandidates = 4, MaxAdjudicationsPerIngestion = 2 };
        options.Policies["EVENT"] = new() { AllowContextualConfirmation = true, ReviewContextRootedCandidates = optIn };
        var ingestion = new IdentityIngestion(IdentityConfiguration.Capture(options), TimeProvider.System,
            provider ?? _candidates, store, embeddings, ids, adjudicator: _model);
        _ = candidateType;
        return ingestion.PlanAsync(new() { Name = "Cutover", Type = "EVENT", Confidence = 1 },
            new() { Targets = [Message("current", CurrentText)], Context = withContext ? [Message("prior", PriorText)] : [] },
            MemoryScope.For("bruna", false), default);
    }

    [Fact]
    public async Task A_same_type_entity_observed_in_a_context_turn_is_reviewed_and_the_adjudicator_decides()
    {
        var plan = await Plan();
        _approximate.Should().ContainSingle().Which.ContextSourceMessageIds.Should().Equal("prior");
        plan.Decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        plan.Decision.ConfirmedEntityId.Should().Be("event-a");
        plan.Decision.Observation.SourceMessageIds.Should().Equal("current");
        _modelCalls.Should().Be(1);
    }

    [Fact]
    public async Task A_quoted_different_verdict_on_a_proximity_only_candidate_leaves_no_proposal()
    {
        // ILC-17 dates control: "Sofia" stayed Pending against "Bruna" (nominated only by turn proximity) after the
        // adjudicator answered "different" with quotes, filling the review queue with proposals already judged.
        _model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(
            "{\"decision\":\"different\",\"candidateId\":\"event-a\",\"evidence\":[{\"sourceId\":\"current\",\"quote\":\"The cutover happens on-site\"}]}");
        var plan = await Plan();
        plan.Decision.Status.Should().Be(IdentityDecisionKind.New);
        plan.Decision.PendingEntityIds.Should().BeEmpty();
        plan.Decision.Diagnostics.Should().Contain("contextual_different");
        plan.Decision.ContextualEvidence!.Verdict.Should().Be(IdentityAdjudicationVerdict.Different);
    }

    [Fact]
    public async Task A_different_verdict_on_a_same_name_candidate_stays_for_review()
    {
        var named = Substitute.For<IIdentityCandidateProvider>();
        named.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true, ContextRootedLookup = true });
        named.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>()).Returns(new IdentityCandidateSet
        { ExactIdentityLookupComplete = true, Candidates = [new() { Entity = new() { EntityId = "event-a", Name = "Cutover", Type = "EVENT",
            OwnerId = "bruna", Confidence = 1, SourceMessageIds = ["prior"], CreatedAtUtc = DateTimeOffset.UnixEpoch } }] });
        named.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        _model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(
            "{\"decision\":\"different\",\"candidateId\":\"event-a\",\"evidence\":[{\"sourceId\":\"current\",\"quote\":\"The cutover happens on-site\"}]}");
        var plan = await Plan(provider: named);
        plan.Decision.Status.Should().Be(IdentityDecisionKind.Pending, "a same-name proposal keeps human review even when the model disagrees");
        plan.Decision.PendingEntityIds.Should().Equal("event-a");
    }

    [Fact]
    public async Task Without_the_type_opt_in_no_context_ids_are_sent_and_the_mention_stays_new()
    {
        var plan = await Plan(optIn: false);
        _approximate.Should().OnlyContain(query => query.ContextSourceMessageIds.Count == 0);
        plan.Decision.Status.Should().Be(IdentityDecisionKind.New);
        _modelCalls.Should().Be(0);
    }

    [Fact]
    public async Task The_current_turn_is_never_sent_as_context()
    {
        await Plan(withContext: false);
        _approximate.Should().OnlyContain(query => query.ContextSourceMessageIds.Count == 0);
    }

    [Fact]
    public void A_context_rooted_signal_alone_never_confirms()
    {
        var candidate = new IdentityCandidate { Entity = new() { EntityId = "event-a", Name = "Acme go-live", Type = "EVENT", OwnerId = "bruna",
            Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch }, Signals = new() { ContextRooted = true } };
        var mention = new ExtractedEntity { Name = "Cutover", Type = "EVENT", Confidence = 1 };
        var context = new EntityIdentityResolutionContext { SourceMessageIds = ["current"] };
        var reviewed = IdentityPolicy.Evaluate(mention, [candidate], context, new() { ReviewContextRootedCandidates = true }, MemoryScope.For("bruna", false));
        reviewed.Decision.Should().Be(IdentityDecisionKind.Pending);
        reviewed.CanAdjudicate.Should().BeTrue();
        IdentityPolicy.Evaluate(mention, [candidate], context, new(), MemoryScope.For("bruna", false)).Decision.Should().Be(IdentityDecisionKind.New);
    }

    [Fact]
    public void A_context_rooted_candidate_of_another_type_is_still_vetoed()
    {
        var candidate = new IdentityCandidate { Entity = new() { EntityId = "org", Name = "Acme Logistics", Type = "ORGANIZATION", OwnerId = "bruna",
            Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch }, Signals = new() { ContextRooted = true } };
        var result = IdentityPolicy.Evaluate(new ExtractedEntity { Name = "Cutover", Type = "EVENT", Confidence = 1 }, [candidate],
            new EntityIdentityResolutionContext { SourceMessageIds = ["current"] }, new() { ReviewContextRootedCandidates = true }, MemoryScope.For("bruna", false));
        result.Decision.Should().Be(IdentityDecisionKind.New);
        result.RejectedEntityIds.Should().Equal("org");
    }

    [Fact]
    public async Task An_opt_in_without_the_backend_capability_fails_configuration()
    {
        var limited = Substitute.For<IIdentityCandidateProvider>();
        limited.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true });
        await FluentActions.Awaiting(() => Plan(provider: limited)).Should().ThrowAsync<OptionsValidationException>();
    }
}
