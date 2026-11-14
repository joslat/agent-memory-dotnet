using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Services;
using FluentAssertions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// ILC-30: the full identity round trip. An identity store's records are copied to the new owner with every reference
/// rewritten (record ids, entity and fact ids, observation keys and owner scopes, inside stored JSON too), and every value
/// keeps its type.
/// </summary>
public sealed class OwnerIdentityPortabilityTests
{
    private const string OldKey = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private static OwnerIdentityPortability.Remap Remap() => new("pablito", "copy-owner",
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["identity-decision-" + OldKey] = "identity-decision-NEW",
            ["identity-observation-" + OldKey] = "identity-observation-NEW",
            ["fact-1"] = "fact-1-new",
            ["entity-1"] = "entity-1-new",
            ["message-1"] = "imported-message-1",
        },
        new Dictionary<string, string>(StringComparer.Ordinal) { [OldKey] = "NEWKEY" });

    private static OwnerGraphValue Text(string text) => new() { Kind = OwnerGraphValue.String, Text = text };

    [Fact]
    public void A_new_observation_key_has_the_shape_of_the_old_and_is_new_for_each_owner_and_each_import()
    {
        var key = OwnerIdentityPortability.NewObservationKey("copy-owner", "import-1", OldKey);
        key.Should().MatchRegex("^[0-9a-f]{64}$").And.NotBe(OldKey);
        OwnerIdentityPortability.NewObservationKey("copy-owner", "import-1", OldKey).Should().Be(key, "one import is reproducible");
        OwnerIdentityPortability.NewObservationKey("another-owner", "import-1", OldKey).Should().NotBe(key);
        OwnerIdentityPortability.NewObservationKey("copy-owner", "import-2", OldKey).Should().NotBe(key,
            "PR260-F5: importing the same file into the same owner again is a second copy, never the first one's ids");
    }

    [Fact]
    public void Owner_scope_ids_and_keys_are_rewritten_by_property()
    {
        var remap = Remap();
        remap.Value("owner_id", Text("pablito")).Text.Should().Be("copy-owner");
        remap.Value("owner_scope", Text("owned:pablito")).Text.Should().Be("owned:copy-owner");
        remap.Value("identity_observation_key", Text(OldKey)).Text.Should().Be("NEWKEY");
        remap.Value("identity_decision_id", Text("identity-decision-" + OldKey)).Text.Should().Be("identity-decision-NEW");
        remap.Value("identity_source_ids", new OwnerGraphValue { Kind = OwnerGraphValue.List, Items = [Text("message-1"), Text("unknown-1")] })
            .Items!.Select(item => item.Text).Should().Equal(["imported-message-1", "unknown-1"], "only what the import maps is rewritten");
    }

    [Fact]
    public void A_name_that_happens_to_equal_the_owner_is_left_alone()
    {
        Remap().Value("name", Text("pablito")).Text.Should().Be("pablito");
        var json = Remap().Value("identity_record_json", Text("""{"Observation":{"Name":"pablito","OwnerId":"pablito"}}""")).Text!;
        json.Should().Contain("\"Name\":\"pablito\"").And.Contain("\"OwnerId\":\"copy-owner\"");
    }

    [Fact]
    public void References_inside_stored_json_are_rewritten()
    {
        var record = $$$"""
            {"DecisionId":"identity-decision-{{{OldKey}}}","ObservationKey":"{{{OldKey}}}",
             "Observation":{"EntityId":"identity-observation-{{{OldKey}}}","OwnerId":"pablito"},
             "Bindings":[{"FactId":"fact-1"}],"Scope":"owned:pablito","Context":{"SourceMessageIds":["message-1"]}}
            """;
        var json = Remap().Value("identity_record_json", Text(record)).Text!;
        json.Should().Contain("identity-decision-NEW").And.Contain("\"NEWKEY\"").And.Contain("identity-observation-NEW")
            .And.Contain("fact-1-new").And.Contain("owned:copy-owner").And.Contain("imported-message-1")
            .And.NotContain(OldKey).And.NotContain("pablito");
    }

    [Fact]
    public void References_in_json_stored_inside_json_and_in_its_property_names_are_rewritten()
    {
        // PR260-F7: a decision's receipt is JSON kept as a string inside the decision's JSON, and a contextual confirmation's
        // fingerprints are keyed by candidate id.
        const string receipt = """{"candidates":[{"id":"entity-1","ownerId":"pablito"}],"contextualCandidateSnapshots":{"entity-1":"hash"}}""";
        var record = System.Text.Json.JsonSerializer.Serialize(new { DecisionId = "identity-decision-" + OldKey, EvidenceJson = receipt });

        var json = Remap().Value("identity_record_json", Text(record)).Text!;

        var evidence = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("EvidenceJson").GetString()!;
        evidence.Should().Contain("\"id\":\"entity-1-new\"").And.Contain("\"entity-1-new\":\"hash\"")
            .And.Contain("\"ownerId\":\"copy-owner\"").And.NotContain("\"entity-1\"").And.NotContain("pablito");
    }

    [Fact]
    public void Every_value_keeps_its_type_through_the_round_trip()
    {
        var at = new ZonedDateTime(new DateTimeOffset(2026, 11, 13, 21, 30, 5, TimeSpan.Zero));
        object?[] values = ["text", 7L, 0.25, true, at, new List<object> { "a", 2L }];
        foreach (var value in values)
        {
            var back = OwnerIdentityPortability.FromValue(OwnerIdentityPortability.ToValue(value));
            switch (value)
            {
                case ZonedDateTime zoned: back.Should().BeOfType<DateTimeOffset>().Which.Should().Be(zoned.ToDateTimeOffset()); break;
                case List<object> list: back.Should().BeAssignableTo<IEnumerable<object?>>().Which.Should().Equal(list); break;
                default: back.Should().Be(value).And.BeOfType(value!.GetType()); break;
            }
        }
    }
}
