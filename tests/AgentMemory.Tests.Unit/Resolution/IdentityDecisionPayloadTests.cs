using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Core.Resolution;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityDecisionPayloadTests
{
    internal static IdentityDecisionWrite Input() => new()
    {
        Decision = new()
        {
            DecisionId = "decision", ObservationKey = "root:mention", Status = IdentityDecisionKind.New, Policy = new(),
            Observation = new() { EntityId = "observation", OwnerId = "pablito", Name = "José", Type = "PERSON", Confidence = 1,
                SourceMessageIds = ["message"], CreatedAtUtc = DateTimeOffset.UnixEpoch },
            Context = new() { SourceMessageIds = ["message"], Sources = [new() { SourceId = "message", Text = "José is arriving." }] },
            CreatedAtUtc = DateTimeOffset.UnixEpoch, UpdatedAtUtc = DateTimeOffset.UnixEpoch
        }
    };

    [Fact]
    public void Capture_freezes_mutable_host_collections_and_retains_original_words_and_roots()
    {
        var ids = new List<string> { "message" }; var attributes = new Dictionary<string, object> { ["office"] = "Córdoba" };
        var input = Input(); input = input with { Decision = input.Decision with
        { Observation = input.Decision.Observation with { Attributes = attributes }, Context = input.Decision.Context with { SourceMessageIds = ids } } };
        var frozen = IdentityDecisionPayload.Capture(input, "pablito");
        ids[0] = "changed"; attributes["office"] = "Bariloche";
        frozen.Decision.Context.SourceMessageIds.Should().Equal("message");
        ((JsonElement)frozen.Decision.Observation.Attributes["office"]).GetString().Should().Be("Córdoba");
        frozen.Decision.Observation.Name.Should().Be("José");
        IdentityDecisionPayload.Decode(IdentityDecisionPayload.Encode(frozen.Decision)).Should().BeEquivalentTo(frozen.Decision,
            options => options.Using<JsonElement>(context => context.Subject.GetRawText().Should().Be(context.Expectation.GetRawText())).WhenTypeIs<JsonElement>());
    }

    [Theory]
    [InlineData(null)] [InlineData("other")] [InlineData("*")]
    public void A_private_observation_cannot_be_written_in_a_different_or_shared_scope(string? owner) =>
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(Input(), owner)).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(0)] [InlineData(2)] [InlineData(-1)]
    public void Initial_writes_cannot_impersonate_a_review_revision(long version)
    {
        var input = Input(); input = input with { Decision = input.Decision with { Version = version } };
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(input, "pablito")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Opaque_json_claiming_same_is_not_a_substitute_for_an_offered_target()
    {
        var input = Input(); input = input with { Decision = input.Decision with
        { Status = IdentityDecisionKind.Confirmed, ConfirmedEntityId = "invented", EvidenceJson = "{\"decision\":\"same\"}" } };
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(input, "pablito")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Pending_cannot_smuggle_an_unoffered_target()
    {
        var input = Input(); input = input with { Decision = input.Decision with { Status = IdentityDecisionKind.Pending, PendingEntityIds = ["foreign"] } };
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(input, "pablito")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Missing_source_provenance_is_rejected()
    {
        var input = Input(); input = input with { Decision = input.Decision with
        { Observation = input.Decision.Observation with { SourceMessageIds = [] }, Context = new() } };
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(input, "pablito")).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(IdentityFactRole.Subject)] [InlineData(IdentityFactRole.Object)]
    public void A_stable_stored_fact_id_and_original_role_survive_capture(IdentityFactRole role)
    {
        var frozen = IdentityDecisionPayload.Capture(Input() with { Bindings = [new() { FactId = "existing-fact", Role = role }] }, "pablito");
        frozen.Bindings.Should().ContainSingle().Which.Should().BeEquivalentTo(new IdentityFactBinding { FactId = "existing-fact", Role = role });
    }

    [Fact]
    public void Unsupported_roles_are_rejected()
    {
        var input = Input() with { Bindings = [new() { FactId = "fact", Role = (IdentityFactRole)31 }] };
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(input, "pablito")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_oversize_combined_context_fails_before_a_large_json_payload_can_be_allocated()
    {
        var input = Input(); input = input with { Decision = input.Decision with { Context = input.Decision.Context with
        { Sources = Enumerable.Range(0, 48).Select(i => new IdentitySourceFragment { SourceId = "source:" + i, Text = new string('x', 32768) }).ToArray() } } };
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(input, "pablito")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void An_oversize_json_element_is_rejected_without_unbounded_serialization()
    {
        using var document = JsonDocument.Parse("\"" + new string('x', 2 * IdentityDecisionPayload.MaxBytes) + "\"");
        var input = Input(); input = input with { Decision = input.Decision with
        { Observation = input.Decision.Observation with { Attributes = new Dictionary<string, object> { ["blob"] = document.RootElement } } } };
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(input, "pablito")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Arbitrary_host_objects_are_rejected_without_invoking_their_getters()
    {
        var poison = new Poison(); var input = Input(); input = input with { Decision = input.Decision with
        { Observation = input.Decision.Observation with { Attributes = new Dictionary<string, object> { ["payload"] = poison } } } };
        FluentActions.Invoking(() => IdentityDecisionPayload.Capture(input, "pablito")).Should().Throw<ArgumentException>();
        poison.GetterCalls.Should().Be(0);
    }

    private sealed class Poison
    {
        public int GetterCalls;
        public string Value { get { GetterCalls++; throw new InvalidOperationException("Must not serialize this host object."); } }
    }
}
