using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityRelationshipPlanningTests
{
    private readonly IIdentityCandidateProvider _candidates = Substitute.For<IIdentityCandidateProvider>();
    private readonly IIdentityDecisionRepository _store = Substitute.For<IIdentityDecisionRepository>();
    private static readonly ExtractedRelationship Original = new() { SourceEntity="Alex", TargetEntity="Acme", RelationshipType="WORKS_FOR" };
    private IdentityIngestion Sut(bool supported)
    {
        _store.IsEnabled.Returns(true); _store.SupportsAtomicIngestion.Returns(true); _store.SupportsRelationshipIngestion.Returns(supported);
        _candidates.Capabilities.Returns(new IdentityCandidateCapabilities { BoundedReads=true,CalendarValueLookup=true });
        var sut = new IdentityIngestion(IdentityConfiguration.Capture(new() { Enabled=true,MaxAdjudicationsPerIngestion=0 }),TimeProvider.System,
            _candidates,_store,Substitute.For<IEmbeddingOrchestrator>(),Substitute.For<IIdGenerator>());
        _store.ClearReceivedCalls(); _candidates.ClearReceivedCalls(); return sut;
    }

    [Fact]
    public void No_relationships_do_not_inspect_the_optional_backend_capability()
    {
        Sut(false).PrepareRelationshipObservations([], ["message"], "pablito").Should().BeEmpty();
        _store.ReceivedCalls().Should().BeEmpty(); _candidates.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public void Unsupported_relationship_storage_is_rejected_before_attributes_or_identity_work()
    {
        var sut=Sut(false);
        Action capture=()=>sut.PrepareRelationshipObservations([Original with
        { Attributes=new Dictionary<string,object> { ["poison"]=new Poison() } }],["message"],"pablito");
        capture.Should().Throw<NotSupportedException>(); _candidates.ReceivedCalls().Should().BeEmpty();
    }
    private sealed class Poison { public string Value=>throw new InvalidOperationException("The getter must not execute."); }

    [Fact]
    public void Original_relationships_are_frozen_and_root_order_confidence_and_time_do_not_change_the_stable_ID()
    {
        var sut=Sut(true); var data=new Dictionary<string,object> { ["team"]="Acme" };
        var roots=new List<string> { "b","a" };
        var first=sut.PrepareRelationshipObservations([Original with { Attributes=data }],roots,"pablito").Single();
        data["team"]="Other"; roots.Clear();
        first.SourceMessageIds.Should().Equal("b","a"); first.Observation.Attributes["team"].ToString().Should().Be("Acme");
        var second=sut.PrepareRelationshipObservations([Original with { Confidence=.8,
            Attributes=new Dictionary<string,object> { ["team"]="Acme" } }],["a","b"],"pablito").Single();
        first.ObservationId.Should().Be(second.ObservationId); _candidates.ReceivedCalls().Should().BeEmpty();
    }

    private static IdentityDecisionWrite Plan(string name,string id)
    {
        var input=IdentityDecisionPayloadTests.Input();
        return input with { Decision=input.Decision with { DecisionId=id,ObservationKey="key-"+id,
            Observation=input.Decision.Observation with { EntityId="observation-"+id,Name=name } } };
    }

    [Fact]
    public void A_homonymous_source_keeps_no_guessed_role_while_the_unique_target_is_retained()
    {
        var drafts=Sut(true).PrepareRelationshipObservations([Original],["message"],"pablito");
        var result=IdentityIngestion.WithRelationshipEndpoints(drafts,[Plan("Alex","a"),Plan("Alex","b"),Plan("Acme","target")]).Single();
        result.SourceDecisionId.Should().BeNull(); result.TargetDecisionId.Should().Be("target");
        result.Observation.SourceEntity.Should().Be("Alex");
    }

    [Fact]
    public void An_idempotently_repeated_plan_does_not_create_false_endpoint_ambiguity()
    {
        var drafts=Sut(true).PrepareRelationshipObservations([Original],["message"],"pablito");
        var plan=Plan("Alex","source");
        var result=IdentityIngestion.WithRelationshipEndpoints(drafts,[plan,plan,Plan("Acme","target")]).Single();
        result.SourceDecisionId.Should().Be("source"); result.TargetDecisionId.Should().Be("target");
    }

    [Fact]
    public void Excess_relationships_fail_before_per_record_capture_or_identity_work()
    {
        var sut=Sut(true);
        Action capture=()=>sut.PrepareRelationshipObservations(Enumerable.Repeat(Original,257).ToArray(),["message"],"pablito");
        capture.Should().Throw<ArgumentException>(); _candidates.ReceivedCalls().Should().BeEmpty();
    }
}
