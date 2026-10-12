using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using AgentMemory.RouterArena.Ask;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class CompoundEvaluationTests
{
    [Fact]
    public async Task Paired_rows_share_one_baseline_and_report_cumulative_physical_requests()
    {
        var calls = 0;
        var gate = Substitute.For<IMemoryGate>();
        gate.DecideAsync(Arg.Any<MemoryGateRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            calls++;
            return new MemoryGateDecision(new Dictionary<string, double>
                { ["first"] = calls == 1 ? .9 : .1, ["second"] = calls == 1 ? .1 : .9 }, "fake");
        });
        var planner = Planner();

        var (baseline, compound) = await CompoundEvaluation.RunCaseAsync(Case(), DateTimeOffset.UnixEpoch,
            new MemoryGateOptions(), gate, planner, () => new(calls * 2, 0, 0, 0, null, null));

        baseline.Kept.Should().Equal("first");
        compound.Kept.Should().Equal("first", "second");
        baseline.Calls.Should().Be(2);
        compound.Calls.Should().Be(6);
        calls.Should().Be(3, "the compound arm reuses the completed baseline rather than asking for it again");
        compound.ReportedInputTokens.Should().BeNull("unreported usage must not become zero-cost evidence");
    }

    [Fact]
    public async Task Baseline_failure_is_retained_in_both_rows_without_calling_planner()
    {
        var gate = Substitute.For<IMemoryGate>();
        gate.DecideAsync(Arg.Any<MemoryGateRequest>(), Arg.Any<CancellationToken>())
            .Returns<Task<MemoryGateDecision>>(_ => throw new HttpRequestException("private provider details"));
        var planner = Planner();

        var (baseline, compound) = await CompoundEvaluation.RunCaseAsync(Case(), DateTimeOffset.UnixEpoch,
            new MemoryGateOptions(), gate, planner, () => new(1, 1, 0, 0, null, null));

        baseline.Error.Should().Be("baseline-HttpRequestException");
        compound.Error.Should().Be(baseline.Error);
        baseline.Calls.Should().Be(1);
        compound.Kept.Should().BeEmpty();
        await planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default);
    }

    [Fact]
    public async Task Partial_failure_keeps_completed_admissions_and_marks_the_failure()
    {
        var calls = 0;
        var gate = Substitute.For<IMemoryGate>();
        gate.DecideAsync(Arg.Any<MemoryGateRequest>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            if (++calls == 3) throw new InvalidOperationException("failed part");
            return new MemoryGateDecision(new Dictionary<string, double>
                { ["first"] = .9, ["second"] = calls == 1 ? .1 : .9 }, "fake");
        });

        var (_, compound) = await CompoundEvaluation.RunCaseAsync(Case(), DateTimeOffset.UnixEpoch,
            new MemoryGateOptions(), gate, Planner(), () => new(calls, 1, 0, 0, null, null));

        compound.Kept.Should().Equal("first", "second");
        compound.Fallback.Should().BeTrue();
        compound.Error.Should().Be("part-failed");
        compound.Compound!.CompletedParts.Should().Be(1);
    }

    [Fact]
    public async Task Incomplete_baseline_is_an_evaluation_failure_not_an_empty_success()
    {
        var gate = Substitute.For<IMemoryGate>();
        gate.DecideAsync(Arg.Any<MemoryGateRequest>(), Arg.Any<CancellationToken>())
            .Returns(new MemoryGateDecision(new Dictionary<string, double> { ["first"] = .9 }, "fake"));

        var (baseline, _) = await CompoundEvaluation.RunCaseAsync(Case(), DateTimeOffset.UnixEpoch,
            new MemoryGateOptions(), gate, Planner(), () => CompoundRequestCounts.Empty);

        baseline.Error.Should().Be("baseline-invalid-decision");
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_to_a_scored_row()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var action = () => CompoundEvaluation.RunCaseAsync(Case(), DateTimeOffset.UnixEpoch,
            new MemoryGateOptions(), Substitute.For<IMemoryGate>(), Planner(), () => CompoundRequestCounts.Empty, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static CompoundCase Case() => new()
    {
        Id = "case", Question = "Who owns the task and when is it due?",
        Candidates = [new("first", "semantic", "A owns the task"), new("second", "prospective", "The task is due tomorrow")],
    };
    private static ICompoundQuestionPlanner Planner()
    {
        var planner = Substitute.For<ICompoundQuestionPlanner>();
        planner.PlanAsync(Arg.Any<CompoundQuestionRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(["Who owns the task?", "When is the task due?"]));
        return planner;
    }
}
