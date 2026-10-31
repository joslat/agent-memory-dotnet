using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// PACK-gate-stats-2 (review 4.16), under the hedged statistic (review 5): the files one task offers are correlated, so
/// precision's confidence bound treats each task as one observation. With one item per task it is about the exact bound for
/// items; when tasks are all right or all wrong it counts tasks, not items; a few tasks never pass for many items.
/// </summary>
public sealed class TrialClusteringTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void With_one_item_per_task_the_bound_is_about_the_exact_one_for_items()
    {
        var tasks = Enumerable.Range(0, 40).Select(i => new PackTrialTask(1, i % 2)).ToList();

        PackTrialGate.PrecisionLower(tasks, 1, 0.95).Should().BeApproximately(PackTrialGate.RateLower(20, 40, 0.95), 0.01);
    }

    [Fact]
    public void When_whole_tasks_are_right_or_wrong_the_bound_counts_tasks_not_items()
    {
        // 40 items, but only 4 independent observations: two tasks wholly right, two wholly wrong.
        PackTrialTask[] tasks = [new(10, 10), new(10, 0), new(10, 10), new(10, 0)];

        PackTrialGate.RateLower(20, 40, 0.95).Should().BeApproximately(0.361, 0.001);
        PackTrialGate.PrecisionLower(tasks, 10, 0.95).Should().BeLessThan(0.25, "four tasks say far less than forty independent items");
    }

    [Fact]
    public void A_few_tasks_never_pass_for_many_items_and_one_wrong_item_does_not_help()
    {
        PackTrialGate.PrecisionLower([], 3, 0.95).Should().Be(0);
        PackTrialGate.PrecisionLower([new(5, 5)], 5, 0.95).Should().BeLessThan(0.3, "one task is one observation");
        // The red team's case: two tasks with every item right among forty; each is one observation, and an unseen task could
        // offer as many items, all wrong.
        static List<PackTrialTask> Two(int offered, int correct) =>
            [.. Enumerable.Range(0, 40).Select(i => i switch { 0 => new PackTrialTask(offered, correct), 1 => new PackTrialTask(1, 1), _ => new PackTrialTask(0, 0) })];
        var allRight = PackTrialGate.PrecisionLower(Two(16, 16), 16, 0.95);
        allRight.Should().BeLessThan(0.1);
        PackTrialGate.PrecisionLower(Two(16, 15), 16, 0.95).Should().BeLessThanOrEqualTo(allRight + 1e-9);
    }

    [Fact]
    public void A_task_offers_at_most_its_arms_cap()
    {
        FluentActions.Invoking(() => PackTrialGate.PrecisionLower([new(4, 1)], 3, 0.95)).Should().Throw<ArgumentException>().WithMessage("*cap*");
        FluentActions.Invoking(() => PackTrialGate.PrecisionLower([new(1, 1)], 17, 0.95)).Should().Throw<ArgumentOutOfRangeException>();
        FluentActions.Invoking(() => PackTrialGate.PrecisionLower([new(1, 1)], 3, 0.5)).Should().Throw<ArgumentOutOfRangeException>().WithMessage("*confidence*");
    }

    [Fact]
    public void A_decision_with_tasks_kept_bounds_precision_over_tasks()
    {
        var manifest = ModuleManifest.Parse("""
            {"format":"agentmemory.module/v1alpha1","id":"com.example.base","version":"1.0.0","sdk":"[0.1,0.2)",
             "trial":{"measure":"test-impact","tasks":12,"confidencePermille":950,"targets":{"precisionMicro":250000}}}
            """);
        var perTask = Enumerable.Range(0, 12).Select(i => i < 4 ? new PackTrialTask(10, 10) : new PackTrialTask(10, 0)).ToList();
        var with = new PackTrialMeasurement { Tasks = 12, Hits = 4, Offered = 120, Correct = 40, P95Ms = 10 };
        var without = new PackTrialMeasurement { Tasks = 12, Hits = 0, Offered = 0, Correct = 0, P95Ms = 5 };

        var clustered = PackTrialGate.Decide(manifest, with with { PerTask = perTask }, without, Now);

        // Review 6, M6: as 120 independent items, 0.333 bounded above 0.25 and passed; without the tasks nothing is bounded now.
        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, with, without, Now)).Should().Throw<ArgumentException>().WithMessage("*PerTask*");
        clustered.Checks.Single().Met.Should().BeFalse("12 tasks wholly right or wrong do not");
        clustered.Checks.Single().Estimate.Should().Be(0.333);
        clustered.With!.PerTask.Should().HaveCount(12, "the record keeps what the bound was computed from");
        clustered.Statistic.Should().Be(PackTrialGate.Statistic);
    }

    [Fact]
    public void Tasks_kept_must_add_up_to_the_arm_and_name_its_hits()
    {
        var manifest = ModuleManifest.Parse("""{"format":"agentmemory.module/v1alpha1","id":"com.example.base","version":"1.0.0","sdk":"[0.1,0.2)","trial":{"measure":"test-impact","tasks":12,"targets":{"precisionMicro":250000}}}""");
        var with = new PackTrialMeasurement { Tasks = 2, Hits = 1, Offered = 4, Correct = 2, P95Ms = 10, PerTask = [new(2, 2), new(1, 0)] };
        var without = new PackTrialMeasurement { Tasks = 2, Hits = 0, Offered = 0, Correct = 0, P95Ms = 5 };

        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, with, without, Now)).Should().Throw<ArgumentException>().WithMessage("*PerTask*");
        // Review 5, L9: hits that disagree with the per-task results.
        var hits = new PackTrialMeasurement { Tasks = 2, Hits = 2, Offered = 3, Correct = 2, P95Ms = 10, PerTask = [new(2, 2), new(1, 0)] };
        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, hits, without, Now)).Should().Throw<ArgumentException>().WithMessage("*Hits*");
        var capped = new PackTrialMeasurement { Tasks = 2, Hits = 1, Offered = 3, Correct = 2, P95Ms = 10, PerTask = [new(2, 2), new(1, 0)], MaximumOffered = 1 };
        FluentActions.Invoking(() => PackTrialGate.Decide(manifest, capped, without, Now)).Should().Throw<ArgumentException>().WithMessage("*cap*");
    }
}
