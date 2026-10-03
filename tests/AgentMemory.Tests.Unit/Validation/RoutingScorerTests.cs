using System.Text.Json;
using AgentMemory.Validation.Routing;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Validation;

/// <summary>40.61: the routing scorer, on a set built by hand (the frozen set is private: its turns come from DemoBrain).</summary>
public sealed class RoutingScorerTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("routing-").FullName;

    private static readonly RoutingSet Set = new()
    {
        Format = "routing-set/1",
        Kinds = ["facts", "graph", "preferences"],
        Items =
        [
            Item("job", [["facts", "graph"]], acceptable: []),
            Item("brother", [["graph"], ["facts"]], acceptable: []),
            Item("coffee", [["preferences"]], acceptable: ["facts"]),
            Item("hello", [], acceptable: []),
            Item("held", [["facts"]], acceptable: [], split: "heldout"),
        ],
    };

    [Fact]
    public void Reading_everything_serves_every_answer_and_wastes_the_rest()
    {
        var score = RoutingScorer.Score(Set, RoutingScorer.Everything(Set));

        score.Items.Should().Be(4, "dev only");
        score.FullySatisfied.Should().Be(3);
        score.GroupRecall.Should().Be(1);
        score.MeanReads.Should().Be(3);
        // job: preferences wasted (1); brother: preferences (1); coffee: graph (1, facts is acceptable); hello: all three.
        score.MeanWastedReads.Should().Be((1 + 1 + 1 + 3) / 4.0);
        score.SilentWhenNothingNeeded.Should().Be(0);
    }

    [Fact]
    public void A_group_is_served_by_any_of_its_kinds_and_every_group_is_needed()
    {
        IReadOnlySet<string> FactsOnly(RoutingItem _) => new HashSet<string> { "facts" };

        var score = RoutingScorer.Score(Set, FactsOnly);

        score.Rows.Single(r => r.Id == "job").Satisfied.Should().Be(1, "facts serves the facts-or-graph group");
        score.Rows.Single(r => r.Id == "brother").Satisfied.Should().Be(1, "the graph group is not served");
        score.Rows.Single(r => r.Id == "coffee").Wasted.Should().Be(0, "facts may hold a preference");
        score.FullySatisfied.Should().Be(1);
    }

    [Fact]
    public void Staying_silent_is_counted_on_turns_that_need_nothing()
    {
        var score = RoutingScorer.Score(Set, item => item.Needs.Count == 0 ? new HashSet<string>() : new HashSet<string>(Set.Kinds));

        score.SilentWhenNothingNeeded.Should().Be(1);
        score.NothingNeeded.Should().Be(1);
        score.PerKind.Single(k => k.Kind == "preferences").ReadWhenNotNeeded.Should().Be(2, "job and brother");
    }

    [Fact]
    public void A_set_read_from_a_file_carries_its_sha256_and_refuses_unknown_kinds()
    {
        var path = Path.Combine(_directory, "set.json");
        File.WriteAllText(path, JsonSerializer.Serialize(Set));

        RoutingSet.Read(path).Sha256.Should().MatchRegex("^[0-9a-f]{64}$");

        File.WriteAllText(path, JsonSerializer.Serialize(Set with { Kinds = ["facts"] }));
        var act = () => RoutingSet.Read(path);
        act.Should().Throw<JsonException>().WithMessage("*names kind 'graph'*");
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static RoutingItem Item(string id, string[][] needs, string[] acceptable, string split = "dev") => new()
    {
        Id = id, Text = id, Split = split, Needs = needs, Acceptable = acceptable,
    };
}
