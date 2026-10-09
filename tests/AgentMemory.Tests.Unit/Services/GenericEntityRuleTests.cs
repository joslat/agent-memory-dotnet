using FluentAssertions;
using AgentMemory.Neo4j.Services;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Dreaming round 2's P4s as the library runs it (AMDREAM001): the cases the router arena measured, including the one that
/// made P4 into P4s (world 6's backpack, held only by its entity).
/// </summary>
public sealed class GenericEntityRuleTests
{
    [Theory]
    [InlineData("School backpack", "OBJECT", true)]
    [InlineData("new phone", "THING", true)]                       // a thing without a name: lower-case, not a person
    [InlineData("Lisbon", "LOCATION", false)]                      // a named place
    [InlineData("mum", "PERSON", false)]                           // a person, even unnamed
    [InlineData("the big red family car outside", "OBJECT", false)] // more than four words: may hold a memory
    [InlineData("Joris | is | a great climber", "OBJECT", false)]  // a fact stored as an entity
    [InlineData("backpack", "", false)]                            // no type
    [InlineData("Car", "VEHICLE, OBJECT", true)]                   // several types, one of them OBJECT
    public void An_entity_is_generic_by_shape_and_kind(string name, string type, bool generic)
    {
        GenericEntityRule.IsGeneric(name, type).Should().Be(generic);
    }

    [Fact]
    public void A_generic_entity_a_fact_names_is_closed_and_one_no_fact_names_is_spared()
    {
        var entities = new[]
        {
            new GenericEntityRule.EntityRow("e1", "o1", "School backpack", "OBJECT"),
            new GenericEntityRule.EntityRow("e2", "o1", "New school backpack", "OBJECT"),
            new GenericEntityRule.EntityRow("e3", "o1", "Vasco", "PERSON"),
        };
        var statements = new (string?, string)[]
        {
            ("o1", "Vasco | carries | his school backpack"),
            ("o1", "Vasco loves his first day of school"),
        };

        var decision = GenericEntityRule.Decide(entities, statements);

        decision.Closed.Select(e => e.Id).Should().Equal("e1");
        decision.Spared.Select(e => e.Id).Should().Equal("e2"); // only the entity says "new school backpack": it stays
    }

    [Fact]
    public void Only_the_owners_own_facts_name_an_entity()
    {
        var entities = new[] { new GenericEntityRule.EntityRow("e1", "o1", "bike", "OBJECT") };
        var statements = new (string?, string)[] { ("o2", "Someone else | rides | a bike") };

        GenericEntityRule.Decide(entities, statements).Closed.Should().BeEmpty();
    }

    [Fact]
    public void Names_match_as_whole_words_across_punctuation_and_case()
    {
        GenericEntityRule.Normalize("Coffee_Machine (kitchen)!").Should().Be("coffee machine kitchen");

        var entities = new[]
        {
            new GenericEntityRule.EntityRow("e1", "o1", "coffee machine", "OBJECT"),
            new GenericEntityRule.EntityRow("e2", "o1", "car", "OBJECT"),
        };
        var statements = new (string?, string)[] { ("o1", "user | fixed | the Coffee-Machine; a carpet arrived") };

        var decision = GenericEntityRule.Decide(entities, statements);

        decision.Closed.Select(e => e.Id).Should().Equal("e1");
        decision.Spared.Select(e => e.Id).Should().Equal("e2"); // "carpet" does not name "car"
    }
}
