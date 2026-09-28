using AgentMemory.Core.Extraction;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>36.4: which value one extraction leaves current, decided once for both write paths.</summary>
public sealed class CurrentValuesTests
{
    /// <summary>A statement: its value, the relation it states (null: multi-valued), and what it replaces.</summary>
    private sealed record S(string Value, string? Relation = "home", string? Replaces = null);

    private static IReadOnlyDictionary<string, string> Replaced(params S[] said) =>
        CurrentValues.Replaced(said, s => s.Value, s => s.Relation, (statement, correction) => correction.Replaces == statement.Value);

    [Fact]
    public void The_last_value_said_is_current_and_replaces_the_others()
    {
        Replaced(new S("Copenhagen"), new S("Oslo")).Should().Equal(new Dictionary<string, string> { ["Copenhagen"] = "Oslo" });
    }

    [Fact]
    public void A_value_said_again_is_current_again()
    {
        Replaced(new S("Copenhagen"), new S("Oslo"), new S("Copenhagen"))
            .Should().Equal(new Dictionary<string, string> { ["Oslo"] = "Copenhagen" });
    }

    [Fact]
    public void A_value_a_correction_names_is_never_current_whatever_the_order()
    {
        Replaced(new S("Oslo", Replaces: "Copenhagen"), new S("Copenhagen"))
            .Should().Equal(new Dictionary<string, string> { ["Copenhagen"] = "Oslo" });
    }

    [Fact]
    public void A_correction_that_is_itself_corrected_passes_on_what_it_replaces()
    {
        Replaced(new S("Oslo", Replaces: "Copenhagen"), new S("Copenhagen", Replaces: "Berlin"), new S("Berlin"))
            .Should().Equal(new Dictionary<string, string> { ["Copenhagen"] = "Oslo", ["Berlin"] = "Oslo" });
    }

    [Fact]
    public void A_multi_valued_statement_is_replaced_only_when_a_correction_names_it()
    {
        Replaced(new S("jazz", Relation: null), new S("rock", Relation: null, Replaces: "jazz"), new S("folk", Relation: null))
            .Should().Equal(new Dictionary<string, string> { ["jazz"] = "rock" });
    }

    [Fact]
    public void When_every_value_is_named_old_the_last_said_stays_current()
    {
        Replaced(new S("A", Replaces: "B"), new S("B", Replaces: "A"))
            .Should().Equal(new Dictionary<string, string> { ["A"] = "B" });
    }

    [Fact]
    public void Two_corrections_naming_each_other_under_no_relation_replace_nothing()
    {
        Replaced(new S("A", Relation: null, Replaces: "B"), new S("B", Relation: null, Replaces: "A")).Should().BeEmpty();
    }

    [Fact]
    public void Separate_relations_are_decided_separately()
    {
        Replaced(new S("Oslo"), new S("Arcade Fire", Relation: "band"), new S("Bergen"), new S("Muse", Relation: "band"))
            .Should().Equal(new Dictionary<string, string> { ["Oslo"] = "Bergen", ["Arcade Fire"] = "Muse" });
    }
}
