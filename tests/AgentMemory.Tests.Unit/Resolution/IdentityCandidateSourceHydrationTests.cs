using System.Text.Json;
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
/// ILC-09 (F1, D1, D4): an eligible contextual decision sees the candidate's own source text, loaded through the
/// owner-scoped source capability, never invented from its name or ID; the hydrated context is the one recorded with
/// the decision, and the original provenance stays the current turn. Reproduces the full Bariloche call that offered
/// the found candidate with "sourceIds": [] and only the cutover sentence.
/// </summary>
public sealed class IdentityCandidateSourceHydrationTests
{
    private const string EventName = "Acme billing system go-live";
    private const string PriorText = "I am leading Acme billing system go-live on December 11, 2026.";
    private const string CurrentText = "The cutover happens on-site in Cordoba, and I have to be there in person.";

    private readonly IIdentityAdjudicator _model = Substitute.For<IIdentityAdjudicator>();
    private readonly IIdentitySourceProvider _sources = Substitute.For<IIdentitySourceProvider>();
    private readonly List<string> _sent = [];

    public IdentityCandidateSourceHydrationTests()
    {
        _model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var data = call.Arg<IdentityAdjudicationRequest>().DataJson;
            _sent.Add(data);
            using var document = JsonDocument.Parse(data);
            var hasPrior = document.RootElement.GetProperty("sources").EnumerateArray()
                .Any(source => source.GetProperty("SourceId").GetString() == "prior");
            return hasPrior
                ? "{\"decision\":\"same\",\"candidateId\":\"event-a\",\"evidence\":[{\"sourceId\":\"prior\",\"quote\":\"Acme billing system go-live\"},{\"sourceId\":\"current\",\"quote\":\"The cutover happens on-site\"}]}"
                : "{\"decision\":\"uncertain\",\"candidateId\":null,\"evidence\":[]}";
        });
        // The fixture holds the actual prior text for the owner's own message only.
        _sources.GetSourcesAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlyList<IdentitySourceFragment>)[.. call.ArgAt<IReadOnlyList<string>>(0)
                .Where(id => id == "prior" && call.ArgAt<string?>(1) == "pablito")
                .Select(id => new IdentitySourceFragment { SourceId = id, Text = PriorText, OriginIds = [id] })]);
    }

    private async Task<IdentityDecision> Plan(bool withSources = true, int calls = 1, string candidateSource = "prior",
        int maxContextBytes = 8_192)
    {
        var candidates = Substitute.For<IIdentityCandidateProvider>();
        candidates.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true });
        candidates.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>()).Returns(new IdentityCandidateSet
        { ExactIdentityLookupComplete = true, Candidates = [new() { Entity = new() { EntityId = "event-a", Name = EventName,
            Type = "EVENT", OwnerId = "pablito", SourceMessageIds = [candidateSource], CreatedAtUtc = DateTimeOffset.UnixEpoch, Confidence = 1 } }] });
        candidates.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        var store = Substitute.For<IIdentityDecisionRepository>();
        store.IsEnabled.Returns(true); store.SupportsAtomicIngestion.Returns(true);
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedEntityAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());
        var ids = Substitute.For<IIdGenerator>(); ids.GenerateId().Returns("temporary");
        var options = new IdentityResolutionOptions { Enabled = true, MaxCandidates = 4, MaxAdjudicationsPerIngestion = calls,
            MaxContextUtf8Bytes = maxContextBytes };
        options.Policies["EVENT"] = new() { AllowContextualConfirmation = true };
        var ingestion = new IdentityIngestion(IdentityConfiguration.Capture(options), TimeProvider.System,
            candidates, store, embeddings, ids, adjudicator: calls > 0 ? _model : null, sources: withSources ? _sources : null);
        var result = await ingestion.PlanAsync(new() { Name = EventName, Type = "EVENT", Confidence = 1 },
            new() { Targets = [new() { MessageId = "current", Content = CurrentText, Role = "user", SessionId = "s",
                ConversationId = "c", TimestampUtc = DateTimeOffset.UnixEpoch }] },
            MemoryScope.For("pablito", false), default);
        return result.Decision;
    }

    [Fact]
    public async Task Candidate_source_roots_are_hydrated_before_the_contextual_decision()
    {
        var decision = await Plan();
        using var document = JsonDocument.Parse(_sent.Should().ContainSingle().Which);
        document.RootElement.GetProperty("candidates")[0].GetProperty("sourceIds").EnumerateArray()
            .Select(source => source.GetString()).Should().Equal("prior");
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed);
        decision.ConfirmedEntityId.Should().Be("event-a");
        decision.Diagnostics.Should().Contain("contextual_candidate_sources_hydrated");
    }

    [Fact]
    public async Task Hydrated_context_is_recorded_while_original_provenance_stays_the_current_turn()
    {
        var decision = await Plan();
        decision.Observation.SourceMessageIds.Should().Equal("current");
        decision.Context.SourceMessageIds.Should().Equal("current");
        decision.Context.Sources.Select(source => source.SourceId).Should().Equal("current", "prior");
        decision.Context.Sources.Single(source => source.SourceId == "prior").Text.Should().Be(PriorText);
        decision.ContextualEvidence!.Citations.Should().Contain(citation => citation.SourceId == "prior");
    }

    [Fact]
    public async Task The_loader_is_asked_only_for_the_candidate_roots_within_the_decision_owner_scope()
    {
        await Plan();
        await _sources.Received(1).GetSourcesAsync(Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(new[] { "prior" })),
            "pablito", false, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_source_outside_the_owner_scope_is_reported_missing_and_never_invented()
    {
        var decision = await Plan(candidateSource: "foreign");
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().Contain("contextual_candidate_sources_missing").And.Contain("contextual_uncertain");
        decision.Context.Sources.Select(source => source.SourceId).Should().Equal("current");
    }

    [Fact]
    public async Task Without_a_source_capability_the_decision_is_unchanged()
    {
        var decision = await Plan(withSources: false);
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Diagnostics.Should().Contain("contextual_uncertain").And.NotContain(d => d.StartsWith("contextual_candidate_sources", StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_call_budget_means_no_source_reads()
    {
        await Plan(calls: 0);
        _sources.ReceivedCalls().Should().BeEmpty();
        _sent.Should().BeEmpty();
    }

    [Fact]
    public async Task A_source_beyond_the_byte_budget_is_omitted_and_reported_not_truncated()
    {
        var decision = await Plan(maxContextBytes: System.Text.Encoding.UTF8.GetByteCount(CurrentText) + 16);
        decision.Diagnostics.Should().Contain("contextual_candidate_sources_omitted_budget");
        decision.Context.Sources.Should().NotContain(source => source.SourceId == "prior");
    }
}
