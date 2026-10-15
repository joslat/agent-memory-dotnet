using AgentMemory.Abstractions.Services;
using AgentMemory.RouterArena.Ask;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class PairedGateEvaluationTests
{
    private static MemoryGateRequest Request(params MemoryGateCandidate[] candidates) =>
        new("What changed?", [], DateTimeOffset.UnixEpoch, candidates);

    private static readonly MemoryGateCandidate Original = new("original", "semantic", "The task was planned.");
    private static readonly MemoryGateCandidate Derived = new("derived", "semantic", "Model-derived: the task was cancelled.");

    [Fact]
    public async Task Both_arms_share_the_context_budget_and_report_displaced_originals()
    {
        var calls = new List<MemoryGateRequest>();
        Task<MemoryGateDecision> Decide(MemoryGateRequest request, CancellationToken token)
        {
            calls.Add(request);
            return Task.FromResult(new MemoryGateDecision(request.Candidates.ToDictionary(c => c.Key, _ => .9), "fake"));
        }
        var budget = PairedGateEvaluation.Render([Derived]).Length;
        var pair = await PairedGateEvaluation.RunAsync(Request(Original), Request(Derived, Original), .23, budget,
            TimeSpan.FromSeconds(1), Decide);

        calls.Should().HaveCount(2);
        pair.Baseline.Selected.Should().Equal("original");
        pair.Overlay.Selected.Should().Equal("derived");
        pair.Overlay.BudgetRejected.Should().Equal("original");
        pair.DisplacedOriginals.Should().Equal("original");
        pair.Baseline.Context!.Length.Should().BeLessThanOrEqualTo(budget);
        pair.Overlay.Context!.Length.Should().BeLessThanOrEqualTo(budget);
        pair.Overlay.Context.Should().Contain("Model-derived");
    }

    [Fact]
    public async Task Identical_requests_reuse_the_same_observation_without_a_second_model_call()
    {
        var calls = 0;
        var pair = await PairedGateEvaluation.RunAsync(Request(Original), Request(Original), .23, 500,
            TimeSpan.FromSeconds(1), (request, _) =>
            {
                calls++;
                return Task.FromResult(new MemoryGateDecision(new Dictionary<string, double> { ["original"] = .5 }, "fake"));
            });
        calls.Should().Be(1);
        pair.ReusedBaseline.Should().BeTrue();
        pair.Overlay.Should().BeSameAs(pair.Baseline);
    }

    [Theory]
    [InlineData("text")]
    [InlineData("missing")]
    [InlineData("question")]
    [InlineData("conversation")]
    [InlineData("time")]
    public async Task Invalid_pair_is_rejected_before_any_dispatch(string change)
    {
        var calls = 0;
        var overlay = change switch
        {
            "text" => Request(Original with { Text = "Changed original" }),
            "missing" => Request(Derived),
            "question" => Request(Original) with { Turn = "A different question" },
            "conversation" => Request(Original) with { Conversation = [new("user", "Extra hint")] },
            _ => Request(Original) with { Now = DateTimeOffset.UnixEpoch.AddDays(1) },
        };
        var act = () => PairedGateEvaluation.RunAsync(Request(Original), overlay, .23, 500,
            TimeSpan.FromSeconds(1), (_, _) => { calls++; throw new InvalidOperationException(); });
        await act.Should().ThrowAsync<ArgumentException>();
        calls.Should().Be(0);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(1.01)]
    [InlineData(-.01)]
    public async Task Invalid_scores_are_failures_not_successful_empty_context(double score)
    {
        var pair = await PairedGateEvaluation.RunAsync(Request(Original), Request(Original), .23, 500,
            TimeSpan.FromSeconds(1), (_, _) => Task.FromResult(new MemoryGateDecision(
                new Dictionary<string, double> { ["original"] = score }, "fake")));
        pair.Baseline.Error.Should().Be("invalid-decision");
        pair.Baseline.Context.Should().BeNull();
        pair.DisplacedOriginals.Should().BeNull();
    }

    [Fact]
    public async Task Partial_decision_and_timeout_do_not_become_evidence_of_rejection()
    {
        var pair = await PairedGateEvaluation.RunAsync(Request(Original), Request(Derived, Original), .23, 500,
            TimeSpan.FromMilliseconds(50), async (request, token) =>
            {
                if (request.Candidates.Count == 2) await Task.Delay(5000, token);
                return new MemoryGateDecision(new Dictionary<string, double>(), "fake");
            });
        pair.Baseline.Error.Should().Be("invalid-decision");
        pair.Overlay.Error.Should().Be("timeout");
        pair.Baseline.GateRejected.Should().BeEmpty();
        pair.Overlay.GateRejected.Should().BeEmpty();
        pair.DisplacedOriginals.Should().BeNull();
    }

    [Fact]
    public async Task Gate_rejection_is_distinct_from_budget_rejection_and_pool_order_is_retained()
    {
        var later = new MemoryGateCandidate("later", "semantic", "Small");
        var oversized = new MemoryGateCandidate("large", "semantic", new string('x', 1000));
        var pool = Request(Original, oversized, later);
        var pair = await PairedGateEvaluation.RunAsync(pool, pool, .23, 150,
            TimeSpan.FromSeconds(1), (_, _) => Task.FromResult(new MemoryGateDecision(
                new Dictionary<string, double> { ["original"] = .1, ["large"] = 1, ["later"] = .3 }, "fake")));
        pair.Baseline.GateRejected.Should().Equal("original");
        pair.Baseline.BudgetRejected.Should().Equal("large");
        pair.Baseline.Selected.Should().Equal("later");
    }

    [Fact]
    public async Task Caller_cancellation_stops_before_dispatch()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        var act = () => PairedGateEvaluation.RunAsync(Request(Original), Request(Original), .23, 500,
            TimeSpan.FromSeconds(1), (_, _) => throw new InvalidOperationException(), source.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
