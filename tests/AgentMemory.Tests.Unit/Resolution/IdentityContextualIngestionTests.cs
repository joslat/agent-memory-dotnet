using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityContextualIngestionTests
{
    private sealed class Fixture : IDisposable
    {
        public readonly IIdentityCandidateProvider Candidates = Substitute.For<IIdentityCandidateProvider>();
        public readonly IIdentityDecisionRepository Store = Substitute.For<IIdentityDecisionRepository>();
        public readonly IIdentityAdjudicator Model = Substitute.For<IIdentityAdjudicator>();
        public readonly ServiceProvider Provider;
        public int Constructed;
        public Fixture(bool enabled = true, int maximum = 1, bool allow = false, bool register = true)
        {
            Candidates.Capabilities.Returns(new IdentityCandidateCapabilities
            { BoundedReads = true, CalendarValueLookup = true, VerifiedKeyLookup = true });
            Candidates.FindExactCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<CancellationToken>())
                .Returns(call => new IdentityCandidateSet { ExactIdentityLookupComplete = true,
                    Candidates = [new() { Entity = new() { EntityId = "a", Name = call.Arg<IdentityCandidateQuery>().Name,
                        Type = "PERSON", OwnerId = "pablito", Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch } }] });
            Candidates.FindApproximateCandidatesAsync(Arg.Any<IdentityCandidateQuery>(), Arg.Any<float[]?>(), Arg.Any<CancellationToken>())
                .Returns(new IdentityCandidateSet { ExactIdentityLookupComplete = true });
            Store.IsEnabled.Returns(true); Store.SupportsAtomicIngestion.Returns(true);
            var embeddings = Substitute.For<IEmbeddingOrchestrator>();
            embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Array.Empty<float>());
            Model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(Same);
            var services = new ServiceCollection(); services.AddLogging(); services.AddAgentMemoryCore(_ => { });
            services.Configure<IdentityResolutionOptions>(options =>
            { options.Enabled = enabled; options.MaxAdjudicationsPerIngestion = maximum;
                options.Policies["PERSON"] = new() { AllowContextualConfirmation = allow }; });
            services.AddSingleton(Candidates); services.AddSingleton(Store); services.AddSingleton(embeddings);
            if (register) services.AddScoped<IIdentityAdjudicator>(_ => { Constructed++; return Model; });
            Provider = services.BuildServiceProvider();
        }
        public int Calls => Model.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "GetDecisionAsync");
        public ExtractionStage Stage(IServiceProvider provider) => new([], [], [], [], [], Substitute.For<IEntityResolver>(),
            Options.Create(new ExtractionOptions()), NullLogger<ExtractionStage>.Instance,
            identity: provider.GetRequiredService<IdentityIngestion>());
        public void Dispose() => Provider.Dispose();
    }
    private const string Same = "{\"decision\":\"same\",\"candidateId\":\"a\",\"evidence\":[{\"sourceId\":\"m\",\"quote\":\"Alex is Alexandra\"}]}";
    private static readonly Message[] Messages = [new() { MessageId = "m", ConversationId = "c", SessionId = "s",
        Role = "user", Content = "Alex is Alexandra. They work in the same team.", TimestampUtc = DateTimeOffset.UnixEpoch }];
    private static Task<ExtractionStageResult> Run(ExtractionStage stage, params string[] names) => stage.ProcessUnifiedAsync(Messages,
        new() { Entities = names.Select(name => new ExtractedEntity { Name = name, Type = "PERSON", Confidence = 1 }).ToArray() },
        ExtractionTypes.Entities, MemoryScope.For("pablito", false));

    [Fact]
    public async Task Eligible_ingestion_calls_the_registered_adjudicator_and_retains_review_evidence()
    {
        using var f = new Fixture(); using var scope = f.Provider.CreateScope();
        var result = await Run(f.Stage(scope.ServiceProvider), "Alex");
        f.Calls.Should().Be(1); f.Constructed.Should().Be(1);
        var decision = result.IdentityPlans.Should().ContainSingle().Subject.Decision;
        decision.Status.Should().Be(IdentityDecisionKind.Pending);
        decision.Observation.Name.Should().Be("Alex");
        decision.Diagnostics.Should().Contain("contextual_same_requires_review");
        var property = typeof(IdentityDecision).GetProperty("ContextualEvidence");
        property.Should().NotBeNull("the durable receipt must be typed separately from opaque caller JSON");
        JsonSerializer.Serialize(property!.GetValue(decision)).Should().Contain("Alex is Alexandra");
    }

    [Fact]
    public async Task One_ingestion_shares_its_call_budget_and_the_next_ingestion_gets_a_new_budget()
    {
        using var f = new Fixture(); using var scope = f.Provider.CreateScope(); var stage = f.Stage(scope.ServiceProvider);
        var first = await Run(stage, "Alex", "Alexander", "Alexa");
        f.Calls.Should().Be(1);
        first.IdentityPlans.Skip(1).Should().OnlyContain(plan => plan.Decision.Diagnostics.Contains("contextual_call_budget"));
        await Run(stage, "Alex"); f.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Concurrent_ingestions_on_the_same_scoped_service_have_independent_budgets()
    {
        using var f = new Fixture(); using var scope = f.Provider.CreateScope(); var stage = f.Stage(scope.ServiceProvider);
        await Task.WhenAll(Run(stage, "Alex", "Alexander"), Run(stage, "Alexa", "Alexandra"));
        f.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Explicit_type_permission_can_propose_confirmation_without_rewriting_the_observation()
    {
        using var f = new Fixture(allow: true); using var scope = f.Provider.CreateScope();
        var result = await Run(f.Stage(scope.ServiceProvider), "Alex");
        var decision = result.IdentityPlans.Single().Decision;
        decision.Status.Should().Be(IdentityDecisionKind.Confirmed); decision.ConfirmedEntityId.Should().Be("a");
        decision.Observation.EntityId.Should().StartWith("identity-observation-"); decision.Observation.Name.Should().Be("Alex");
        f.Store.ReceivedCalls().Count(call => call.GetMethodInfo().Name == "SaveAsync").Should().Be(0);
    }

    [Theory]
    [InlineData(false, 2)] [InlineData(true, 0)]
    public void Off_and_zero_budget_do_not_construct_the_model(bool enabled, int maximum)
    {
        using var f = new Fixture(enabled, maximum); using var scope = f.Provider.CreateScope();
        _ = f.Stage(scope.ServiceProvider); f.Constructed.Should().Be(0); f.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("{\"decision\":")]
    [InlineData("{\"decision\":\"same\",\"candidateId\":\"foreign\",\"evidence\":[]}")]
    [InlineData("{\"decision\":\"same\",\"candidateId\":\"a\",\"evidence\":[{\"sourceId\":\"m\",\"quote\":\"invented\"}]}")]
    public async Task Invalid_model_output_keeps_the_original_observation_pending(string response)
    {
        using var f = new Fixture(allow: true); using var scope = f.Provider.CreateScope();
        f.Model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(response);
        var result = await Run(f.Stage(scope.ServiceProvider), "Alex");
        var decision = result.IdentityPlans.Single().Decision;
        decision.Status.Should().Be(IdentityDecisionKind.Pending); decision.ConfirmedEntityId.Should().BeNull();
        decision.Diagnostics.Should().Contain("contextual_invalid_response"); f.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Missing_optional_client_retains_deterministic_pending_behavior()
    {
        using var f = new Fixture(register: false); using var scope = f.Provider.CreateScope();
        var result = await Run(f.Stage(scope.ServiceProvider), "Alex");
        result.IdentityPlans.Single().Decision.Status.Should().Be(IdentityDecisionKind.Pending); f.Calls.Should().Be(0);
    }
}
