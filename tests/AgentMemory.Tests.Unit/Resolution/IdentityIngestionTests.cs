using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityIngestionTests
{
    private readonly IIdentityCandidateProvider _candidates = Substitute.For<IIdentityCandidateProvider>();
    private readonly IIdentityDecisionRepository _store = Substitute.For<IIdentityDecisionRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IIdGenerator _ids = Substitute.For<IIdGenerator>();
    private static readonly MemoryScope Scope = MemoryScope.For("pablito", false);
    private static ExtractedEntity Mention(string name = "Córdoba") => new() { Name = name, Type = "LOCATION", Confidence = 1 };
    private static Message Source(string id = "m1", string content = "The office is in Córdoba") => new()
    { MessageId = id, ConversationId = "conversation", SessionId = "scenario", Role = "user", Content = content, TimestampUtc = DateTimeOffset.UnixEpoch };
    private static ExtractionWindow Window(params Message[] messages) => ExtractionWindow.ForTargets(messages.Length == 0 ? [Source()] : messages);
    public IdentityIngestionTests()
    {
        _candidates.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true });
        _candidates.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>()).Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        _candidates.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>()).Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        _store.IsEnabled.Returns(true); _store.SupportsAtomicIngestion.Returns(true);
        _ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());
    }
    private IdentityIngestion Sut(IdentityResolutionOptions? options = null) => new(
        IdentityConfiguration.Capture(options ?? new() { Enabled = true, MaxAdjudicationsPerIngestion = 0 }),
        TimeProvider.System, _candidates, _store, _embeddings, _ids);

    [Fact]
    public void Disabled_configuration_does_not_inspect_backend_capabilities()
    {
        var candidates = Substitute.For<IIdentityCandidateProvider>();
        _ = new IdentityIngestion(IdentityConfiguration.Capture(new()), TimeProvider.System, candidates);
        candidates.ReceivedCalls().Should().BeEmpty();
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)]
    public void Enabled_mode_requires_owned_schema_and_atomic_ingestion(bool schema, bool atomic)
    {
        _store.IsEnabled.Returns(schema); _store.SupportsAtomicIngestion.Returns(atomic);
        var action = () => Sut(); action.Should().Throw<InvalidOperationException>();
        _candidates.ReceivedCalls().Count(call => call.GetMethodInfo().Name.StartsWith("Find", StringComparison.Ordinal)).Should().Be(0);
    }
    [Fact]
    public async Task Stable_root_and_mention_ids_ignore_random_resolver_ids_and_clock_changes()
    {
        var first = await Sut().PlanAsync(Mention(), Window(), Scope, default);
        var next = await Sut().PlanAsync(Mention(), Window(), Scope, default);
        first.Decision.DecisionId.Should().Be(next.Decision.DecisionId);
        first.Decision.Observation.EntityId.Should().Be(next.Decision.Observation.EntityId);
        first.Decision.ObservationKey.Should().Be(next.Decision.ObservationKey);
        _store.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "SaveAsync").Should().Be(0);
    }
    [Fact]
    public async Task Policy_revision_does_not_create_another_observation_on_retry()
    {
        var a = new IdentityResolutionOptions { Enabled = true }; a.Policies["LOCATION"] = new() { Version = "1" };
        var b = new IdentityResolutionOptions { Enabled = true }; b.Policies["LOCATION"] = new() { Version = "2" };
        (await Sut(a).PlanAsync(Mention(), Window(), Scope, default)).Decision.ObservationKey.Should()
            .Be((await Sut(b).PlanAsync(Mention(), Window(), Scope, default)).Decision.ObservationKey);
    }
    [Theory]
    [InlineData("pablito")] [InlineData("*")] [InlineData("**")] [InlineData(null)]
    public async Task Owner_is_preserved_and_contributes_to_the_observation_key(string? owner)
    {
        var scope = owner is null ? AgentMemory.Core.Services.SharedScopes.SharedOnly : MemoryScope.For(owner, false);
        var result = await Sut().PlanAsync(Mention(), Window(), scope, default);
        result.Decision.Observation.OwnerId.Should().Be(owner);
        var other = await Sut().PlanAsync(Mention(), Window(), MemoryScope.For("other", false), default);
        result.Decision.ObservationKey.Should().NotBe(other.Decision.ObservationKey);
    }
    [Fact]
    public async Task Homonyms_with_different_descriptions_keep_separate_observation_ids()
    {
        var a = await Sut().PlanAsync(Mention() with { Description = "Argentina" }, Window(), Scope, default);
        var b = await Sut().PlanAsync(Mention() with { Description = "Spain" }, Window(), Scope, default);
        a.Decision.ObservationKey.Should().NotBe(b.Decision.ObservationKey);
    }
    [Fact]
    public async Task Distinct_root_turns_do_not_coalesce_equal_names_into_one_observation()
    {
        var a = await Sut().PlanAsync(Mention(), Window(Source("a")), Scope, default);
        var b = await Sut().PlanAsync(Mention(), Window(Source("b")), Scope, default);
        a.Decision.ObservationKey.Should().NotBe(b.Decision.ObservationKey);
    }
    [Fact]
    public async Task Root_order_does_not_change_the_stable_key()
    {
        var a = await Sut().PlanAsync(Mention(), Window(Source("a"), Source("b")), Scope, default);
        var b = await Sut().PlanAsync(Mention(), Window(Source("b"), Source("a")), Scope, default);
        a.Decision.ObservationKey.Should().Be(b.Decision.ObservationKey);
    }
    [Fact]
    public async Task Attribute_dictionary_order_does_not_change_the_stable_key()
    {
        var a = await Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object> { ["city"] = "Córdoba", ["country"] = "Argentina" } }, Window(), Scope, default);
        var b = await Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object> { ["country"] = "Argentina", ["city"] = "Córdoba" } }, Window(), Scope, default);
        a.Decision.ObservationKey.Should().Be(b.Decision.ObservationKey);
    }
    [Fact]
    public async Task Arbitrary_attribute_getters_are_rejected_before_any_backend_or_embedding_call()
    {
        var action = () => Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object> { ["poison"] = new Poison() } }, Window(), Scope, default);
        await action.Should().ThrowAsync<ArgumentException>();
        _candidates.ReceivedCalls().Count(call => call.GetMethodInfo().Name.StartsWith("Find", StringComparison.Ordinal)).Should().Be(0);
        _embeddings.ReceivedCalls().Should().BeEmpty();
    }
    private sealed class Poison { public string Getter => throw new InvalidOperationException("This getter must never execute."); }
    [Fact]
    public async Task Oversized_source_context_is_omitted_explicitly_without_inventing_provenance()
    {
        var result = await Sut(new() { Enabled = true, MaxContextUtf8Bytes = 4 }).PlanAsync(Mention(), Window(Source(content: "España")), Scope, default);
        result.Decision.Context.Sources.Should().BeEmpty();
        result.Decision.Context.SourceMessageIds.Should().Equal("m1");
        result.Decision.Diagnostics.Should().Contain("identity_sources_omitted_budget");
    }
    [Fact]
    public async Task Cancellation_is_honored_before_backend_or_model_work()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var action = () => Sut().PlanAsync(Mention(), Window(), Scope, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        _candidates.ReceivedCalls().Count(call => call.GetMethodInfo().Name.StartsWith("Find", StringComparison.Ordinal)).Should().Be(0);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(257)]
    public void Mention_budget_is_validated_before_backend_work(int limit)
    {
        var action = () => Sut(new() { Enabled = true, MaxMentionsPerIngestion = limit });
        action.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>();
    }
    [Theory]
    [InlineData("December 3", "December 12")] [InlineData("2026-12-03", "2026-12-12")]
    [InlineData("2026-12-09/2026-12-12", "2026-12-09/2026-12-13")]
    public async Task Different_calendar_values_do_not_share_original_observations(string left, string right)
    {
        var a = await Sut().PlanAsync(Mention(left) with { Type = "OBJECT" }, Window(), Scope, default);
        var b = await Sut().PlanAsync(Mention(right) with { Type = "OBJECT" }, Window(), Scope, default);
        a.Decision.ObservationKey.Should().NotBe(b.Decision.ObservationKey);
        a.Decision.Observation.Name.Should().Be(left); b.Decision.Observation.Name.Should().Be(right);
    }

    [Fact]
    public async Task Caller_owned_transactions_are_rejected_before_candidate_or_embedding_work()
    {
        _store.HasActiveWriteTransaction.Returns(true);
        var action = () => Sut().PlanAsync(Mention(), Window(), Scope, default);
        await action.Should().ThrowAsync<NotSupportedException>();
        _candidates.ReceivedCalls().Count(call => call.GetMethodInfo().Name.StartsWith("Find", StringComparison.Ordinal)).Should().Be(0);
        _embeddings.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void Disabled_mode_does_not_inspect_transaction_state()
    {
        _store.ClearReceivedCalls();
        Sut(new() { Enabled = false }).AssertOwnsTransactionBoundary();
        _store.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Nested_attribute_property_order_does_not_create_another_observation_on_retry()
    {
        var a = await Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object>
            { ["place"] = new Dictionary<string, object> { ["city"] = "Córdoba", ["country"] = "Argentina" } } }, Window(), Scope, default);
        var b = await Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object>
            { ["place"] = new Dictionary<string, object> { ["country"] = "Argentina", ["city"] = "Córdoba" } } }, Window(), Scope, default);
        a.Decision.ObservationKey.Should().Be(b.Decision.ObservationKey);
    }

    [Fact]
    public async Task Nested_attribute_value_changes_preserve_distinct_observations()
    {
        var a = await Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object>
            { ["place"] = new Dictionary<string, object> { ["country"] = "Argentina" } } }, Window(), Scope, default);
        var b = await Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object>
            { ["place"] = new Dictionary<string, object> { ["country"] = "Spain" } } }, Window(), Scope, default);
        a.Decision.ObservationKey.Should().NotBe(b.Decision.ObservationKey);
    }

    [Fact]
    public async Task Attribute_array_order_is_preserved_as_observation_data()
    {
        var a = await Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object> { ["route"] = new object[] { "Córdoba", "Bariloche" } } }, Window(), Scope, default);
        var b = await Sut().PlanAsync(Mention() with { Attributes = new Dictionary<string, object> { ["route"] = new object[] { "Bariloche", "Córdoba" } } }, Window(), Scope, default);
        a.Decision.ObservationKey.Should().NotBe(b.Decision.ObservationKey);
    }
}
