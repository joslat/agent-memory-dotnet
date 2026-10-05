using System.Text.Json;
using AgentMemory.RouterArena.Data;
using AgentMemory.RouterArena.Jar;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.RouterArena;

/// <summary>
/// 40.98: the jar's selection in the arena. One hand-made turn, every family's decision on it; the fold rule, the sign test,
/// the threshold grid and the tie-break. Values cross-checked against the first implementation of the same rules.
/// </summary>
public sealed class JarTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("router-jar-").FullName;

    // Needs F1 and one of E1/E2; P1 helps. Found: F1, F2, E2, P1 (and the working memory W0); today kept F1 and F2.
    private JarTurn Turn()
    {
        Write("j", """{"answers":{"t1":{"yes":{"working|W0":0.9,"semantic|F1":0.9,"semantic|F2":0.1,"entity-graph|E2":0.6,"preference|P1":0.3},"seconds":0.3}}}""");
        Write("l", """{"answers":{"t1":{"yes":{"semantic|F1":0.1,"semantic|F2":0.9,"entity-graph|E2":0.0},"seconds":0.02}}}""");
        var candidates = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>("""
            {"t1":{
              "wide":[["W0","working",null,1],["F1","semantic",0.8,1],["F2","semantic",0.6,2],["E2","entity-graph",0.5,1],["P1","preference",0.7,1]],
              "today":[["F1","semantic",0.8,1],["F2","semantic",0.6,2]],
              "reading":[["F1","temporal",0.8,1]]}}
            """)!;
        var item = new MatrixItem { Id = "t1", Text = "what did I tell you about it?", Needs = [["F1"], ["E1", "E2"]], Acceptable = ["P1"] };
        return JarTurn.Of(new JarSet("s", _directory, [item], candidates)).Single();
    }

    [Fact]
    public void A_turn_knows_what_was_found_what_it_needs_and_what_today_kept()
    {
        var turn = Turn();

        turn.Servable.Should().BeTrue("F1 and E2 were found");
        turn.ClutterFound.Should().Be(1, "F2 is the only memory found that the turn neither needs nor accepts");
        turn.Today.Should().BeEquivalentTo(["F1", "F2"]);
        turn.Scored.Select(s => s.Memory).Should().Equal("F1", "F1", "P1", "F2", "E2");
        turn.Scored[0].Door.Should().Be("temporal", "equal scores keep the higher id, then the higher type name, first");
    }

    [Theory]
    [InlineData("{\"family\":\"today\"}", "F1,F2")]
    [InlineData("{\"family\":\"gate\",\"judge\":\"j\",\"t\":0.5}", "E2,F1")]
    [InlineData("{\"family\":\"gate\",\"judge\":\"missing\",\"t\":0.5}", "F1,F2")]
    [InlineData("{\"family\":\"blend\",\"online\":\"j\",\"local\":\"l\",\"w\":0.8,\"t\":0.5}", "F1")]
    [InlineData("{\"family\":\"hybrid\",\"local\":\"l\",\"online\":\"j\",\"low\":0.2,\"high\":0.8,\"t\":0.5}", "F2")]
    [InlineData("{\"family\":\"hybrid\",\"local\":\"l\",\"online\":\"j\",\"low\":0.05,\"high\":0.8,\"t\":0.5}", "F1")]
    [InlineData("{\"family\":\"module\",\"judge\":\"j\",\"t\":0.5}", "E2,F1,F2")]
    [InlineData("{\"family\":\"cospool\",\"k\":2}", "F1,P1")]
    [InlineData("{\"family\":\"cosfloor\",\"f\":0.6}", "F1,F2,P1")]
    [InlineData("{\"family\":\"cosdoor\",\"k\":1}", "E2,F1,P1")]
    public void Every_family_decides_as_written(string form, string admitted)
    {
        var (got, _) = JsonSerializer.Deserialize<JarForm>(form)!.Admit(Turn());

        got.Order(StringComparer.Ordinal).Should().Equal(admitted.Split(','), "the working memory is never scored; a judge with no answer fails open to today");
    }

    [Fact]
    public void The_hybrid_waits_for_the_online_judge_only_when_it_asked_it()
    {
        var turn = Turn();

        new JarForm { Family = "hybrid", Local = "l", Online = "j", Low = 0.2, High = 0.8, T = 0.5 }.Admit(turn).Seconds.Should().Be(0.02);
        new JarForm { Family = "hybrid", Local = "l", Online = "j", Low = 0.05, High = 0.8, T = 0.5 }.Admit(turn).Seconds.Should().BeApproximately(0.32, 1e-12);
        new JarForm { Family = "blend", Local = "l", Online = "j", W = 0.8, T = 0.5 }.Admit(turn).Seconds.Should().Be(0.3, "both are asked at once");
    }

    [Fact]
    public void A_score_counts_served_and_clutter_at_the_prompt()
    {
        var turn = Turn();

        JarScore.Score(new JarForm { Family = "gate", Judge = "j", T = 0.5 }, [turn])
            .Should().BeEquivalentTo(new JarResult(1, 1, 100.0, 0, 0, 339));
        JarScore.Score(JarForm.Today, [turn]).Should().BeEquivalentTo(new JarResult(0, 1, 0, 1, 100.0, 0), "today misses E2 and lets F2 in");
    }

    [Fact]
    public void The_pick_takes_the_most_served_within_the_budget_and_ties_to_less_clutter()
    {
        (int, int, int, int)[][] rows =
        [
            [(1, 1, 9, 10), (1, 1, 0, 10)],     // 2 served, 45% clutter: over the budget
            [(1, 1, 1, 10), (0, 1, 0, 10)],     // 1 served, 5%
            [(1, 1, 0, 10), (0, 1, 0, 10)],     // 1 served, 0%: wins the tie
        ];

        JarScore.Pick(rows, [0, 1], budget: 6.0).Should().Be(2);
        JarScore.Pick(rows, [0, 1], budget: 50.0).Should().Be(0);
        JarScore.Pick(rows, [0, 1], budget: -1).Should().BeNull("no setting fits");
    }

    [Theory]
    [InlineData("m4:self-fact:01", 1)]
    [InlineData("w2:self-fact:01", 4)]
    [InlineData("m:a:01", 2)]
    [InlineData("x", 0)]
    public void A_turn_s_fold_is_its_id_s_sha256_mod_five(string id, int fold) => JarTurn.FoldOf(id).Should().Be(fold);

    [Theory]
    [InlineData(29, 0, 3.725290298461914e-09)]
    [InlineData(66, 4, 1.6502251632246126e-15)]
    [InlineData(24, 5, 0.0005461126565933228)]
    [InlineData(52, 50, 0.921191049253872)]
    [InlineData(8, 0, 0.0078125)]
    [InlineData(0, 0, 1.0)]
    public void The_sign_test_is_exact_and_two_sided(int wins, int losses, double p) =>
        JarScore.SignTest(wins, losses).Should().BeApproximately(p, p * 1e-9);

    [Fact]
    public void The_threshold_grid_reaches_down_to_a_thousandth()
    {
        JarScore.Grid.Should().HaveCount(101);
        JarScore.Grid.Take(9).Should().Equal(0.001, 0.002, 0.003, 0.005, 0.007, 0.01, 0.015, 0.02, 0.03);
        JarScore.Grid[^1].Should().Be(0.95);
        JarScore.Candidates("blend", local: "l", online: "j").Should().HaveCount(5 * 101);
        JarScore.Candidates("hybrid", local: "l", online: "j").Should().OnlyContain(f => f.High > f.Low);
    }

    private void Write(string judge, string json) => File.WriteAllText(Path.Combine(_directory, $"jev-{judge}.json"), json);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
