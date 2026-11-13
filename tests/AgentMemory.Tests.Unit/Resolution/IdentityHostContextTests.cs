using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityHostContextTests
{
    private readonly IIdentityContextProvider _host = Substitute.For<IIdentityContextProvider>();
    private readonly IIdentityCandidateProvider _candidates = Substitute.For<IIdentityCandidateProvider>();
    private readonly IIdentityDecisionRepository _store = Substitute.For<IIdentityDecisionRepository>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IIdGenerator _ids = Substitute.For<IIdGenerator>();
    private static readonly MemoryScope Scope = MemoryScope.For("pablito", false);
    private static readonly ExtractedEntity Mention = new() { Name = "Alex", Type = "PERSON", Confidence = 1,
        Attributes = new Dictionary<string, object> { ["id"] = "model-guess" } };
    private static readonly ExtractionWindow Window = ExtractionWindow.ForTargets([new Message
    { MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user", Content = "Alex joined the team.", TimestampUtc = DateTimeOffset.UnixEpoch }]);

    public IdentityHostContextTests()
    {
        _candidates.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true });
        _candidates.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>()).Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        _candidates.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>()).Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        _store.IsEnabled.Returns(true); _store.SupportsAtomicIngestion.Returns(true);
        _ids.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
    }

    private IdentityIngestion Sut(IIdentityContextProvider? host) => new(IdentityConfiguration.Capture(new()
    { Enabled = true, MaxAdjudicationsPerIngestion = 0, Policies = new Dictionary<string, EntityIdentityPolicy>
        { ["PERSON"] = new() { IdentityProperties = ["id"] } } }), TimeProvider.System, _candidates, _store, _embeddings, _ids, host);
    private static VerifiedIdentityKey Key(string id = "42") => new()
    { Namespace = "directory", Authority = "fixture:directory", SourceId = "directory:" + id,
      Values = new Dictionary<string, string> { ["id"] = id } };
    private void Returns(IdentityHostEvidence? value) => _host.GetContextAsync(Arg.Any<ExtractedEntity>(),
        Arg.Any<EntityIdentityResolutionContext>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>()).Returns(value);
    private int Reads => _candidates.ReceivedCalls().Count(call => call.GetMethodInfo().Name.StartsWith("Find", StringComparison.Ordinal));

    [Fact]
    public async Task Host_keys_reach_the_query_and_receipt_separately_from_model_attributes()
    {
        Returns(new() { Keys = [Key()] });
        IdentityCandidateQuery? query = null;
        _candidates.FindExactCandidatesAsync(Arg.Do<IdentityCandidateQuery>(q => query = q), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        var result = await Sut(_host).PlanAsync(Mention, Window, Scope, default);
        query!.VerifiedKeys.Should().ContainSingle().Which.Values["id"].Should().Be("42");
        result.Decision.Context.VerifiedKeys.Should().ContainSingle().Which.Values["id"].Should().Be("42");
        result.Decision.Observation.Attributes["id"].ToString().Should().Be("model-guess");
    }

    [Fact]
    public async Task Host_kind_and_namespace_keep_their_authority_and_source()
    {
        var kind = new VerifiedIdentityKind { Kind = IdentitySubjectKind.Reference, Authority = "directory", SourceId = "kind:42" };
        var ns = new VerifiedIdentityNameNamespace { Namespace = "employees", Authority = "directory", SourceId = "names:42" };
        Returns(new() { Kind = kind, NameNamespace = ns });
        var result = await Sut(_host).PlanAsync(Mention, Window, Scope, default);
        result.Decision.Context.VerifiedKind.Should().BeEquivalentTo(kind);
        result.Decision.Context.NameNamespace.Should().Be("employees");
        result.Decision.Context.NameNamespaceEvidence.Should().BeEquivalentTo(ns);
    }

    [Fact]
    public async Task Provider_receives_a_frozen_source_snapshot_in_the_effective_scope()
    {
        EntityIdentityResolutionContext? seen = null; MemoryScope? owner = null; ExtractedEntity? mention = null;
        _host.GetContextAsync(Arg.Any<ExtractedEntity>(), Arg.Any<EntityIdentityResolutionContext>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(call => { mention = call.ArgAt<ExtractedEntity>(0); seen = call.ArgAt<EntityIdentityResolutionContext>(1); owner = call.ArgAt<MemoryScope?>(2); return (IdentityHostEvidence?)null; });
        var result = await Sut(_host).PlanAsync(Mention, Window, Scope, default);
        seen.Should().NotBeNull(); owner.Should().BeEquivalentTo(Scope);
        seen!.Sources.Should().ContainSingle().Which.Text.Should().Be("Alex joined the team.");
        Action roots = () => ((IList<string>)seen.SourceMessageIds).Add("foreign");
        Action origins = () => ((IList<string>)seen.Sources[0].OriginIds).Add("foreign");
        Action attributes = () => ((IDictionary<string, object>)mention!.Attributes).Add("intruder", true);
        roots.Should().Throw<NotSupportedException>(); origins.Should().Throw<NotSupportedException>(); attributes.Should().Throw<NotSupportedException>();
        result.Decision.Context.SourceMessageIds.Should().Equal("m");
    }

    [Fact]
    public async Task Cancellation_while_the_provider_completes_stops_before_any_candidate_read()
    {
        using var cancellation = new CancellationTokenSource();
        _host.GetContextAsync(Arg.Any<ExtractedEntity>(), Arg.Any<EntityIdentityResolutionContext>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(_ => { cancellation.Cancel(); return (IdentityHostEvidence?)null; });
        var action = () => Sut(_host).PlanAsync(Mention, Window, Scope, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>(); Reads.Should().Be(0);
    }

    [Fact]
    public async Task Provider_failure_is_not_silently_replaced_by_unverified_attributes()
    {
        _host.GetContextAsync(Arg.Any<ExtractedEntity>(), Arg.Any<EntityIdentityResolutionContext>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IdentityHostEvidence?>(new InvalidOperationException("directory unavailable")));
        var action = () => Sut(_host).PlanAsync(Mention, Window, Scope, default);
        await action.Should().ThrowAsync<InvalidOperationException>().WithMessage("directory unavailable"); Reads.Should().Be(0);
    }

    [Fact]
    public async Task Mutable_host_values_are_frozen_before_candidate_awaits()
    {
        var values = new Dictionary<string, string> { ["id"] = "42" };
        Returns(new() { Keys = [Key() with { Values = values }] });
        _candidates.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => { values["id"] = "changed"; return new IdentityCandidateSet { ExactIdentityLookupComplete = true }; });
        var result = await Sut(_host).PlanAsync(Mention, Window, Scope, default);
        result.Decision.Context.VerifiedKeys.Should().ContainSingle().Which.Values["id"].Should().Be("42");
    }

    [Fact]
    public async Task Oversize_assertion_sets_fail_before_candidate_reads()
    {
        Returns(new() { Keys = Enumerable.Range(0, 33).Select(i => Key(i.ToString())).ToArray() });
        var action = () => Sut(_host).PlanAsync(Mention, Window, Scope, default);
        await action.Should().ThrowAsync<ArgumentException>(); Reads.Should().Be(0);
    }

    [Fact]
    public async Task A_namespace_without_authority_is_not_accepted_as_verified()
    {
        Returns(new() { NameNamespace = new() { Namespace = "employees", Authority = "", SourceId = "names" } });
        var action = () => Sut(_host).PlanAsync(Mention, Window, Scope, default);
        await action.Should().ThrowAsync<ArgumentException>(); Reads.Should().Be(0);
    }

    [Fact]
    public void Disabled_identity_does_not_resolve_a_registered_host_factory()
    {
        var constructed = 0; var services = new ServiceCollection(); services.AddLogging(); services.AddAgentMemoryCore(_ => { });
        services.AddScoped<IIdentityContextProvider>(_ => { constructed++; throw new InvalidOperationException("must stay dark"); });
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IdentityIngestion>().Enabled.Should().BeFalse(); constructed.Should().Be(0);
    }

    [Fact]
    public async Task Host_evidence_changes_do_not_rekey_the_original_observation()
    {
        Returns(new() { Keys = [Key("42")] }); var first = await Sut(_host).PlanAsync(Mention, Window, Scope, default);
        Returns(new() { Keys = [Key("43")] }); var second = await Sut(_host).PlanAsync(Mention, Window, Scope, default);
        first.Decision.ObservationKey.Should().Be(second.Decision.ObservationKey);
    }

    [Fact]
    public async Task Missing_host_does_not_promote_extractor_attributes()
    {
        var result = await Sut(null).PlanAsync(Mention, Window, Scope, default);
        result.Decision.Context.VerifiedKeys.Should().BeEmpty(); result.Decision.Context.VerifiedKind.Should().BeNull();
        result.Decision.Context.NameNamespaceEvidence.Should().BeNull();
    }
    [Fact]
    public async Task Enabled_DI_resolves_the_scoped_host_and_uses_its_assertions()
    {
        Returns(new() { Keys = [Key()] }); var constructed = 0;
        var services = new ServiceCollection(); services.AddLogging(); services.AddAgentMemoryCore(_ => { });
        services.Configure<IdentityResolutionOptions>(options => { options.Enabled = true; options.MaxAdjudicationsPerIngestion = 0; options.Policies["PERSON"] = new() { IdentityProperties = ["id"] }; });
        services.AddSingleton(_candidates); services.AddSingleton(_store); services.AddSingleton(_embeddings);
        services.AddScoped<IIdentityContextProvider>(_ => { constructed++; return _host; });
        using var provider = services.BuildServiceProvider(); using var scope = provider.CreateScope();
        var result = await scope.ServiceProvider.GetRequiredService<IdentityIngestion>().PlanAsync(Mention, Window, Scope, default);
        constructed.Should().Be(1); result.Decision.Context.VerifiedKeys.Should().ContainSingle();
    }

    [Fact]
    public async Task Incomplete_host_keys_are_retained_as_context_but_not_submitted_as_exact_identity()
    {
        Returns(new() { Keys = [Key() with { Values = new Dictionary<string, string> { ["other"] = "42" } }] });
        IdentityCandidateQuery? query = null;
        _candidates.FindExactCandidatesAsync(Arg.Do<IdentityCandidateQuery>(q => query = q), Arg.Any<CancellationToken>())
            .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
        var result = await Sut(_host).PlanAsync(Mention, Window, Scope, default);
        query!.VerifiedKeys.Should().BeEmpty(); result.Decision.Status.Should().Be(IdentityDecisionKind.New);
    }

    [Theory]
    [InlineData("employees", "other", "directory", "source")]
    [InlineData(null, "employees", "directory", "source")]
    [InlineData("employees", "employees", "", "source")]
    [InlineData("employees", "employees", "directory", "")]
    [InlineData("EMPLOYEES", "employees", "directory", "source")]
    public void Namespace_provenance_cannot_disagree_with_its_claim_or_omit_authority(string? name, string attested, string authority, string source)
    {
        var input = IdentityDecisionPayloadTests.Input();
        input = input with { Decision = input.Decision with { Context = input.Decision.Context with
        { NameNamespace = name, NameNamespaceEvidence = new() { Namespace = attested, Authority = authority, SourceId = source } } } };
        Action capture = () => IdentityDecisionPayload.Capture(input, "pablito");
        capture.Should().Throw<ArgumentException>();
    }

}
