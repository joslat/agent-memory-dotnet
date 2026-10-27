using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-gate-stats-2 (review 4.16): the files one task offers are correlated, so precision's confidence bound treats each
/// task as a cluster. With one item per task it is the plain bound; when tasks are all right or all wrong it falls well
/// below it; it is never above it.
/// </summary>
public sealed class TrialClusteringTests
{
    private const double Z95 = 1.6448536;
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void With_one_item_per_task_the_clustered_bound_is_about_the_plain_one()
    {
        var tasks = Enumerable.Range(0, 40).Select(i => new PackTrialTask(1, i % 2)).ToList();

        PackTrialGate.ClusteredWilsonLower(tasks, Z95).Should().BeApproximately(PackTrialGate.WilsonLower(20, 40, Z95), 0.01);
    }

    [Fact]
    public void When_whole_tasks_are_right_or_wrong_the_bound_counts_tasks_not_items()
    {
        // 40 items, but only 4 independent observations: two tasks wholly right, two wholly wrong.
        PackTrialTask[] tasks = [new(10, 10), new(10, 0), new(10, 10), new(10, 0)];

        var clustered = PackTrialGate.ClusteredWilsonLower(tasks, Z95);

        PackTrialGate.WilsonLower(20, 40, Z95).Should().BeApproximately(0.374, 0.001);
        clustered.Should().BeLessThan(0.25, "four clusters say far less than forty independent items");
    }

    [Fact]
    public void The_clustered_bound_is_never_above_the_plain_one()
    {
        var random = new Random(4_16);
        for (var run = 0; run < 200; run++)
        {
            var tasks = Enumerable.Range(0, random.Next(2, 60)).Select(_ =>
            {
                var offered = random.Next(0, 6);
                return new PackTrialTask(offered, random.Next(0, offered + 1));
            }).ToList();
            var offered = tasks.Sum(t => t.Offered);
            var plain = PackTrialGate.WilsonLower(tasks.Sum(t => t.Correct), offered, Z95);
            PackTrialGate.ClusteredWilsonLower(tasks, Z95).Should().BeLessThanOrEqualTo(plain + 1e-12);
        }
        PackTrialGate.ClusteredWilsonLower([], Z95).Should().Be(0);
        PackTrialGate.ClusteredWilsonLower([new(5, 5)], Z95).Should().BeLessThan(0.3, "one task is one observation");
        // The red team's case: two tasks, 201 files, all correct. No spread to measure: each task is one observation.
        var allRight = Enumerable.Range(0, 40).Select(i => i switch { 0 => new PackTrialTask(200, 200), 1 => new PackTrialTask(1, 1), _ => new PackTrialTask(0, 0) }).ToList();
        PackTrialGate.ClusteredWilsonLower(allRight, Z95).Should().BeApproximately(PackTrialGate.WilsonLower(2, 2, Z95), 1e-9).And.BeLessThan(0.6);
    }

    [Fact]
    public void A_decision_with_tasks_kept_uses_the_clustered_bound_for_precision()
    {
        var manifest = ModuleManifest.Parse("""
            {"format":"agentmemory.module/v1alpha1","id":"com.example.base","version":"1.0.0","sdk":"[0.1,0.2)",
             "trial":{"measure":"test-impact","tasks":12,"confidence":0.95,"targets":{"precision":0.25}}}
            """);
        var perTask = Enumerable.Range(0, 12).Select(i => i < 4 ? new PackTrialTask(10, 10) : new PackTrialTask(10, 0)).ToList();
        var with = new PackTrialMeasurement { Tasks = 12, Hits = 4, Offered = 120, Correct = 40, P95Ms = 10 };
        var without = new PackTrialMeasurement { Tasks = 12, Hits = 0, Offered = 0, Correct = 0, P95Ms = 5 };

        var independent = PackTrialGate.Decide(manifest, with, without, Now);
        var clustered = PackTrialGate.Decide(manifest, with with { PerTask = perTask }, without, Now);

        independent.Checks.Single().Met.Should().BeTrue("120 independent items at 0.333 bound above 0.25");
        clustered.Checks.Single().Met.Should().BeFalse("12 tasks wholly right or wrong do not");
        clustered.Checks.Single().Estimate.Should().Be(0.333);
        clustered.With!.PerTask.Should().HaveCount(12, "the record keeps what the bound was computed from");
    }

    [Fact]
    public void Tasks_kept_must_add_up_to_the_arm()
    {
        var manifest = ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.base","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","tasks":12,"targets":{"precision":0.25}}}""");
        var with = new PackTrialMeasurement { Tasks = 2, Hits = 1, Offered = 4, Correct = 2, P95Ms = 10, PerTask = [new(2, 2), new(1, 0)] };
        var without = new PackTrialMeasurement { Tasks = 2, Hits = 0, Offered = 0, Correct = 0, P95Ms = 5 };

        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, with, without, Now)).Should().Throw<ArgumentException>().WithMessage("*PerTask*");
    }
}
