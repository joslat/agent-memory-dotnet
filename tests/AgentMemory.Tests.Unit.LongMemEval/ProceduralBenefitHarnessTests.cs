using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Whether procedural memory helps an agent repeat a task — and refusing to say yes for the wrong
/// reason (7.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>LongMemEval cannot answer this.</b> It scores answers about a transcript; a promoted procedure
/// changes how an agent <i>works</i> across repeated attempts. With no trace nodes and a probe that
/// was label-blind to them, a promoted procedure was invisible to every instrument here — the feature
/// <i>could not fail</i>, which is the defect shape this harness exists to prevent.
/// </para>
/// <para>
/// The harness is driven by a scripted runner, so the thing that can be quietly wrong is tested
/// without buying the thing that costs money.
/// </para>
/// </remarks>
public sealed class ProceduralBenefitHarnessTests
{
    /// <summary>A scripted agent: outcomes are decided by the test, not by a model.</summary>
    private sealed class ScriptedRunner(Func<bool, int, AgentTaskRun> script) : IAgentTaskRunner
    {
        public readonly List<(bool Enabled, int Attempt)> Calls = [];

        public Task<AgentTaskRun> RunAsync(
            string taskId, bool procedureMemoryEnabled, int attempt, CancellationToken cancellationToken = default)
        {
            Calls.Add((procedureMemoryEnabled, attempt));
            return Task.FromResult(script(procedureMemoryEnabled, attempt));
        }
    }

    private static Task<ProceduralBenefitResult> MeasureAsync(
        Func<bool, int, AgentTaskRun> script, int attempts = 4) =>
        ProceduralBenefitResult.MeasureAsync(new ScriptedRunner(script), "book-a-trip", attempts);

    [Fact]
    public async Task AGenuineBenefitIsReported()
    {
        // The happy path: procedures cut the work and the task still gets done every time.
        var result = await MeasureAsync((enabled, attempt) =>
            enabled && attempt > 1
                ? new AgentTaskRun(true, Steps: 3, ToolCalls: 2)
                : new AgentTaskRun(true, Steps: 9, ToolCalls: 7));

        result.ShowsBenefit.Should().BeTrue();
        result.StepReduction.Should().BeGreaterThan(0);
        result.ImprovedWithRepetition.Should().BeTrue();
    }

    [Fact]
    public async Task GivingUpSoonerIsNotABenefit()
    {
        // THE test. An agent that abandons the task takes fewer steps and makes fewer tool calls, and
        // both read as an improvement in every efficiency measure. Completion rate is what stops that
        // being reported as a win.
        var result = await MeasureAsync((enabled, _) =>
            enabled
                ? new AgentTaskRun(Completed: false, Steps: 2, ToolCalls: 1)
                : new AgentTaskRun(Completed: true, Steps: 9, ToolCalls: 7));

        result.CompletionRateDelta.Should().BeLessThan(0);
        result.ShowsBenefit.Should().BeFalse("fewer steps is not a saving if the task is not done");
    }

    [Fact]
    public async Task EvenASmallCorrectnessLossDisqualifiesTheClaim()
    {
        // Not a hair's tolerance. An agent with the wrong procedure executes confidently, so a
        // completion rate that slipped is a safety finding, not a rounding error to trade away.
        var result = await MeasureAsync((enabled, attempt) =>
            enabled && attempt == 4
                ? new AgentTaskRun(false, 2, 1)
                : new AgentTaskRun(true, enabled ? 3 : 9, enabled ? 2 : 7));

        result.StepReduction.Should().BeGreaterThan(0);
        result.ShowsBenefit.Should().BeFalse();
    }

    [Fact]
    public async Task StartingCheaperIsNotLearning()
    {
        // An arm that is fast from attempt one did not learn a procedure during the run -- something
        // else separates the arms, and reporting it as a memory benefit would be wrong.
        var result = await MeasureAsync((enabled, _) =>
            new AgentTaskRun(true, enabled ? 3 : 9, enabled ? 2 : 7));

        result.StepReduction.Should().BeGreaterThan(0);
        result.ImprovedWithRepetition.Should().BeFalse();
        result.ShowsBenefit.Should().BeFalse(
            "the class has always documented both comparisons as required, and a large step reduction "
            + "with no learning across attempts means the arms differ for some other reason");
    }

    [Fact]
    public async Task LearningShownInToolCallsAloneStillCountsAsLearning()
    {
        // The shape a real run produced: the arm read its procedure, skipped the refused call, and used
        // the same number of turns to do it. A steps-only gate called that "learned nothing" while
        // ToolCallReduction -- from the same class -- reported the saving.
        (int Steps, int Calls)[] procedures = [(6, 6), (6, 5), (6, 5)];
        var result = await MeasureAsync(
            (enabled, attempt) => enabled
                ? new AgentTaskRun(true, procedures[attempt - 1].Steps, procedures[attempt - 1].Calls)
                : new AgentTaskRun(true, Steps: 6, ToolCalls: 6),
            attempts: 3);

        result.ImprovedStepsWithRepetition.Should().BeFalse();
        result.ImprovedToolCallsWithRepetition.Should().BeTrue();
        result.ImprovedWithRepetition.Should().BeTrue();
    }

    [Fact]
    public async Task TradingTurnsForToolCallsIsNotLearning()
    {
        // The other half of the rule. An arm that spent four extra turns to make one fewer call has
        // moved the cost, not learned a cheaper route.
        (int Steps, int Calls)[] procedures = [(6, 6), (9, 5), (10, 5)];
        var result = await MeasureAsync(
            (enabled, attempt) => enabled
                ? new AgentTaskRun(true, procedures[attempt - 1].Steps, procedures[attempt - 1].Calls)
                : new AgentTaskRun(true, Steps: 6, ToolCalls: 6),
            attempts: 3);

        result.ImprovedToolCallsWithRepetition.Should().BeTrue();
        result.ImprovedWithRepetition.Should().BeFalse("steps got worse");
        result.ShowsBenefit.Should().BeFalse();
    }

    [Fact]
    public async Task ANoiseLevelDifferenceIsNotABenefit()
    {
        // The shape a real run produced: 6.3 steps against 6.7, identical tool calls, and a verdict of
        // True. A third of a step across three attempts is the difference between two runs of the SAME
        // configuration, which is what the control arm's own spread measures.
        var procedures = new[] { 7, 6, 6 };
        var control = new[] { 6, 7, 7 };
        var result = await MeasureAsync(
            (enabled, attempt) => new AgentTaskRun(
                true,
                Steps: enabled ? procedures[attempt - 1] : control[attempt - 1],
                ToolCalls: 6),
            attempts: 3);

        result.StepReduction.Should().BeGreaterThan(0, "the means do differ, slightly");
        result.ImprovedWithRepetition.Should().BeTrue("7 -> 6 steps, so the learning gate alone passes");
        result.StepNoiseBand.Should().BeGreaterThan(
            result.WithoutProcedures.MeanStepsWhenCompleted - result.WithProcedures.MeanStepsWhenCompleted);
        result.ShowsBenefit.Should().BeFalse("the saving is smaller than the control arm's own jitter");
    }

    [Fact]
    public async Task ASavingLargerThanTheControlSpreadIsABenefit()
    {
        // The floor must not be so high that a real, consistent saving is discarded: the control varies
        // by one step, the enabled arm saves four.
        var control = new[] { 9, 10, 10 };
        var procedures = new[] { 9, 6, 5 };
        var result = await MeasureAsync(
            (enabled, attempt) => new AgentTaskRun(
                true,
                Steps: enabled ? procedures[attempt - 1] : control[attempt - 1],
                ToolCalls: enabled ? procedures[attempt - 1] : control[attempt - 1]),
            attempts: 3);

        result.StepGainExceedsNoise.Should().BeTrue();
        result.ShowsBenefit.Should().BeTrue();
    }

    [Fact]
    public async Task AControlArmWithASingleCompletedRunHasNoSpreadRatherThanAnInventedOne()
    {
        // One observation has no standard deviation. Manufacturing one would either mask a real gain or
        // conjure a floor out of nothing.
        var result = await MeasureAsync(
            (enabled, attempt) => new AgentTaskRun(
                Completed: enabled || attempt == 1, Steps: enabled ? 3 : 9, ToolCalls: enabled ? 2 : 7),
            attempts: 3);

        result.StepNoiseBand.Should().Be(0);
    }

    [Fact]
    public async Task FailedAttemptsDoNotDragDownTheStepAverage()
    {
        // Averaged over completed runs only: mixing "solved it in three steps" with "gave up after
        // three" treats abandonment as a cheap success.
        var result = await MeasureAsync((enabled, attempt) =>
            enabled && attempt == 1
                ? new AgentTaskRun(false, Steps: 1, ToolCalls: 0)
                : new AgentTaskRun(true, Steps: 4, ToolCalls: 3));

        result.WithProcedures.MeanStepsWhenCompleted.Should().Be(4);
    }

    [Fact]
    public async Task AttemptsRunInOrderAndSequentially()
    {
        // The hypothesis is that attempt N benefits from what attempt N-1 stored. Running them
        // concurrently would race the write the next read depends on, measuring a feature that had
        // not happened yet.
        var runner = new ScriptedRunner((_, _) => new AgentTaskRun(true, 5, 4));

        await ProceduralBenefitResult.MeasureAsync(runner, "book-a-trip", attempts: 3);

        runner.Calls.Where(c => c.Enabled).Select(c => c.Attempt).Should().Equal(1, 2, 3);
        runner.Calls.Where(c => !c.Enabled).Select(c => c.Attempt).Should().Equal(1, 2, 3);
    }

    [Fact]
    public async Task BothArmsAreRun()
    {
        var runner = new ScriptedRunner((_, _) => new AgentTaskRun(true, 5, 4));

        var result = await ProceduralBenefitResult.MeasureAsync(runner, "book-a-trip", attempts: 2);

        result.WithProcedures.Runs.Should().HaveCount(2);
        result.WithoutProcedures.Runs.Should().HaveCount(2);
    }

    [Fact]
    public async Task ASingleAttemptIsRejected()
    {
        // One attempt per arm cannot show learning at all -- there is no run 1 versus run N.
        var act = async () => await ProceduralBenefitResult.MeasureAsync(
            new ScriptedRunner((_, _) => new AgentTaskRun(true, 1, 1)), "t", attempts: 1);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task AnArmThatNeverCompletesReportsZeroRatherThanDividingByZero()
    {
        var result = await MeasureAsync((_, _) => new AgentTaskRun(false, 3, 2));

        result.WithProcedures.MeanStepsWhenCompleted.Should().Be(0);
        result.ShowsBenefit.Should().BeFalse();
    }
}
