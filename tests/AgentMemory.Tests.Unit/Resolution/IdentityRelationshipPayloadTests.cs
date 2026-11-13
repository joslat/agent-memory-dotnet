using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityRelationshipPayloadTests
{
    internal static IdentityRelationshipObservation Input() => new()
    {
        ObservationId = "relationship-observation", OwnerId = "pablito", SourceMessageIds = ["message"],
        Observation = new() { SourceEntity = "Acme go live", TargetEntity = "Córdoba", RelationshipType = "LOCATED_IN",
            Attributes = new Dictionary<string, object> { ["on_site"] = true } },
        SourceDecisionId = "event-decision", TargetDecisionId = "place-decision", CreatedAtUtc = DateTimeOffset.UnixEpoch
    };

    [Fact]
    public void Original_words_and_mutable_collections_are_frozen_and_roundtrip_independently_of_identity()
    {
        var ids = new List<string> { "message" }; var attributes = new Dictionary<string, object> { ["on_site"] = true };
        var input = Input(); input = input with { SourceMessageIds = ids, Observation = input.Observation with { Attributes = attributes } };
        var frozen = IdentityRelationshipPayload.Capture(input, "pablito"); ids.Clear(); attributes["on_site"] = false;
        frozen.SourceMessageIds.Should().Equal("message");
        frozen.Observation.SourceEntity.Should().Be("Acme go live"); frozen.Observation.TargetEntity.Should().Be("Córdoba");
        ((System.Text.Json.JsonElement)frozen.Observation.Attributes["on_site"]).GetBoolean().Should().BeTrue();
        var decoded = IdentityRelationshipPayload.Decode(IdentityRelationshipPayload.Encode(frozen));
        IdentityRelationshipPayload.Fingerprint(decoded).Should().Be(IdentityRelationshipPayload.Fingerprint(frozen));
    }

    [Theory]
    [InlineData(null)] [InlineData("other")] [InlineData("*")]
    public void Relationship_observations_require_the_exact_write_owner(string? owner)
    {
        Action action = () => IdentityRelationshipPayload.Capture(Input(), owner);
        action.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("original")] [InlineData("attributes")] [InlineData("source")]
    [InlineData("target")] [InlineData("type")] [InlineData("sources")]
    [InlineData("many-sources")] [InlineData("confidence")]
    [InlineData("utf8-budget")] [InlineData("decision")]
    public void Invalid_or_oversized_original_claims_fail_at_capture(string change)
    {
        var input = Input();
        input = change switch
        {
            "original" => input with { Observation = null! },
            "attributes" => input with { Observation = input.Observation with { Attributes = null! } },
            "source" => input with { Observation = input.Observation with { SourceEntity = " " } },
            "target" => input with { Observation = input.Observation with { TargetEntity = " " } },
            "type" => input with { Observation = input.Observation with { RelationshipType = " " } },
            "sources" => input with { SourceMessageIds = [] },
            "many-sources" => input with { SourceMessageIds = Enumerable.Repeat("message",257).ToArray() },
            "confidence" => input with { Observation = input.Observation with { Confidence = double.NaN } },
            "utf8-budget" => input with { Observation = input.Observation with { Description = new string('ñ',20000) } },
            _ => input with { SourceDecisionId = " " }
        };
        Action action = () => IdentityRelationshipPayload.Capture(input,"pablito");
        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unsupported_attribute_objects_are_rejected_without_running_getters()
    {
        var input = Input(); input = input with { Observation = input.Observation with
        { Attributes = new Dictionary<string, object> { ["poison"] = new Poison() } } };
        Action action = () => IdentityRelationshipPayload.Capture(input,"pablito");
        action.Should().Throw<ArgumentException>();
    }
    private sealed class Poison { public string Value => throw new InvalidOperationException("The getter must not execute."); }

    [Fact]
    public void Root_and_object_order_do_not_rekey_the_claim_while_array_order_and_original_words_do()
    {
        var input = Input(); input = input with { SourceMessageIds = ["b","a"], Observation = input.Observation with
        { Attributes = new Dictionary<string, object> { ["nested"] = new Dictionary<string, object> { ["b"] = 2, ["a"] = new[] {1,2} } } } };
        var other = input with { SourceMessageIds = ["a","b"], Observation = input.Observation with
        { Attributes = new Dictionary<string, object> { ["nested"] = new Dictionary<string, object> { ["a"] = new[] {1,2}, ["b"] = 2 } } } };
        string Hash(IdentityRelationshipObservation value) => IdentityRelationshipPayload.Fingerprint(IdentityRelationshipPayload.Capture(value,"pablito"));
        Hash(input).Should().Be(Hash(other));
        Hash(input).Should().NotBe(Hash(input with { Observation = input.Observation with { TargetEntity = "Bariloche" } }));
        Hash(input).Should().NotBe(Hash(input with { Observation = input.Observation with
        { Attributes = new Dictionary<string, object> { ["nested"] = new Dictionary<string, object> { ["a"] = new[] {2,1}, ["b"] = 2 } } } }));
    }

    [Fact]
    public void Confidence_time_and_endpoint_interpretations_do_not_rekey_the_original_claim()
    {
        var input = IdentityRelationshipPayload.Capture(Input(),"pablito");
        var changed = input with { SourceDecisionId = null, TargetDecisionId = "later", CreatedAtUtc = DateTimeOffset.UtcNow,
            Observation = input.Observation with { Confidence = .8 } };
        IdentityRelationshipPayload.Fingerprint(changed).Should().Be(IdentityRelationshipPayload.Fingerprint(input));
    }

    [Fact]
    public void Entity_payloads_also_preserve_bounded_primitive_arrays_as_json_data()
    {
        var input = IdentityDecisionPayloadTests.Input();
        input = input with { Decision = input.Decision with { Observation = input.Decision.Observation with
        { Attributes = new Dictionary<string,object> { ["counts"] = new[] {1,2} } } } };
        var frozen = IdentityDecisionPayload.Capture(input,"pablito");
        ((System.Text.Json.JsonElement)frozen.Decision.Observation.Attributes["counts"]).GetArrayLength().Should().Be(2);
    }

    [Theory]
    [InlineData("rank")] [InlineData("count")] [InlineData("non-finite")]
    public void Primitive_arrays_keep_rank_count_and_finite_value_limits(string change)
    {
        object data = change switch
        { "rank" => new int[1,1], "count" => new int[257], _ => new[] {double.NaN} };
        var input = Input(); input = input with { Observation = input.Observation with
        { Attributes = new Dictionary<string,object> { ["counts"] = data } } };
        Action capture = () => IdentityRelationshipPayload.Capture(input,"pablito");
        capture.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Existing_backends_stay_source_compatible_and_do_not_silently_accept_unowned_relationships()
    {
        IIdentityDecisionRepository backend = new ExistingBackend();
        backend.SupportsRelationshipIngestion.Should().BeFalse();
        Func<Task> save = () => backend.SaveRelationshipObservationAsync(Input(),"pablito");
        await save.Should().ThrowAsync<NotSupportedException>();
    }
    private sealed class ExistingBackend : IIdentityDecisionRepository
    {
        public bool IsEnabled => true;
        public Task<IdentityDecision> SaveAsync(IdentityDecisionWrite request,string? ownerId=null,CancellationToken cancellationToken=default) => throw new NotSupportedException();
        public Task<IdentityDecision?> GetAsync(string decisionId,string? ownerId=null,CancellationToken cancellationToken=default) => throw new NotSupportedException();
    }
}
