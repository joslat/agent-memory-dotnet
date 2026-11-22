using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Tests.IdentityKinds;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution.IdentityKinds;

/// <summary>The tier 1 generator of the Identity Kinds Benchmark (IKB-1): what it builds, before anything is run.</summary>
public sealed class IdentityKindCasesTests
{
    private static readonly IdentityKindCases.Case[] Cases = IdentityKindCases.Build().ToArray();

    internal static string BaselinePath([CallerFilePath] string? source = null) =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source!)!, "../../TestData/Identity/identity-kinds-tier1-baseline.json"));

    [Fact]
    public void Every_kind_meets_every_situation_that_applies_to_it_in_134_cases()
    {
        Cases.Should().HaveCount(134);
        Cases.Select(c => c.Id).Should().OnlyHaveUniqueItems();
        Cases.Select(c => c.Kind).Distinct().Should().HaveCount(14);
        Cases.Select(c => c.Situation).Distinct().Should().HaveCount(10);
    }

    [Fact]
    public void A_changed_type_is_tried_only_where_a_model_might_give_another_type()
    {
        Cases.Where(c => c.Situation == "S06-type").Select(c => c.Kind)
            .Should().BeEquivalentTo(IdentityKindCases.Kinds.Where(k => k.AltType != k.Type).Select(k => k.Id));
        foreach (var c in Cases.Where(c => c.Situation == "S06-type"))
        {
            var kind = IdentityKindCases.Kinds.Single(k => k.Id == c.Kind);
            c.Turns.Select(t => t.Mentions[0].Type).Should().Equal(kind.Type, kind.AltType, kind.Type);
        }
    }

    [Fact]
    public void A_person_namesake_is_tried_only_for_things_that_are_not_people()
    {
        Cases.Where(c => c.Situation == "S07-person-namesake").Select(c => c.Kind)
            .Should().BeEquivalentTo(IdentityKindCases.Kinds.Where(k => k.Type != "PERSON").Select(k => k.Id));
        Cases.Where(c => c.Situation == "S07-person-namesake")
            .Should().OnlyContain(c => c.RealThings == 2 && c.Turns[1].Mentions.Single().Type == "PERSON");
    }

    [Fact]
    public void Every_turn_writes_the_names_it_mentions_letter_for_letter()
    {
        // A store-aware writer's reference carries a quote that must be found in the turn.
        foreach (var turn in Cases.SelectMany(c => c.Turns))
            foreach (var mention in turn.Mentions)
                turn.Text.Should().Contain(mention.Name, $"the turn \"{turn.Text}\" mentions {mention.Name}");
    }

    [Fact]
    public void Each_case_counts_the_real_things_its_turns_are_about()
    {
        foreach (var c in Cases)
            c.Turns.SelectMany(t => t.Mentions).Where(m => m.Thing < IdentityKindCases.FillerThing).Select(m => m.Thing).Distinct()
                .Should().HaveCount(c.RealThings, c.Id);
    }

    [Fact]
    public void The_cases_are_the_ones_the_recorded_baseline_ran_in_every_arm()
    {
        using var baseline = JsonDocument.Parse(File.ReadAllText(BaselinePath()));
        var rows = baseline.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => (Case: r.GetProperty("case").GetString()!, Arm: r.GetProperty("arm").GetString()!)).ToList();
        foreach (var arm in new[] { "previous", "rules", "writer" })
            rows.Where(r => r.Arm == arm).Select(r => r.Case).Should().Equal(Cases.Select(c => c.Id), arm);
    }
}
