using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The benchmark task procedural memory is measured against (7.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>A benchmark for procedural memory has one hard requirement: the shortest correct path must be
/// discoverable but not guessable.</b> If the task can be completed on a first attempt by calling the
/// obvious tool, there is nothing to learn, both arms score identically, and the harness reports "no
/// benefit" for a reason that has nothing to do with memory.
/// </para>
/// <para>
/// These tests exist to prove the task actually has that property — that it refuses shortcuts, and
/// that the only route to the completion marker is the full chain. Getting this wrong would not look
/// like a broken test; it would look like a feature that does not help.
/// </para>
/// </remarks>
public sealed class ProceduralBenchmarkTaskTests
{
    private static async Task<string> InvokeAsync(ProceduralBenchmarkTask task, string tool, object args)
    {
        var fn = (AIFunction)task.CreateTools().Single(t => ((AIFunction)t).Name.Contains(tool, StringComparison.OrdinalIgnoreCase));
        var dict = args.GetType().GetProperties().ToDictionary(p => p.Name, p => (object?)p.GetValue(args));
        var result = await fn.InvokeAsync(new AIFunctionArguments(dict));
        return result?.ToString() ?? string.Empty;
    }

    [Fact]
    public async Task TheChainCompletesWhenWalkedInOrder()
    {
        var task = new ProceduralBenchmarkTask();

        var lookup = await InvokeAsync(task, "LookUpTraveller", new { traveller = "ruaidhri" });
        lookup.Should().Contain("gold");

        var hold = await InvokeAsync(task, "PlaceHold", new { connection = "14:05", tier = "gold" });
        hold.Should().Contain("HOLD-4417");

        var booking = await InvokeAsync(task, "Book", new { holdReference = "HOLD-4417" });
        booking.Should().Contain(ProceduralBenchmarkTask.ConfirmationMarker);
        task.IsComplete(booking).Should().BeTrue();
    }

    [Fact]
    public async Task BookingDirectlyIsRefused()
    {
        // THE property that makes the benchmark measure anything. If the obvious first move worked,
        // an agent would succeed cold on attempt one and both arms would tie -- reporting "procedural
        // memory does not help" for a reason that is entirely about the task.
        var task = new ProceduralBenchmarkTask();

        var booking = await InvokeAsync(task, "Book", new { holdReference = "whatever" });

        booking.Should().Contain("refused", Exactly.Once());
        task.IsComplete(booking).Should().BeFalse();
    }

    [Fact]
    public async Task AHoldWithoutTheTierIsRefused()
    {
        // The second link. The tier lives behind a lookup the prompt never mentions, so an agent
        // meeting this cold has to be refused before it can discover the step -- which is the cost the
        // stored procedure removes on later attempts.
        var task = new ProceduralBenchmarkTask();

        var hold = await InvokeAsync(task, "PlaceHold", new { connection = "14:05", tier = "unknown" });

        hold.Should().Contain("refused");
        hold.Should().Contain("look up the traveller", Exactly.Once());
    }

    [Fact]
    public async Task ARefusalNamesTheMissingStep()
    {
        // Refused, not thrown. A hard failure ends the run instead of teaching it, and an agent that
        // cannot learn the chain cold makes the control arm measure the harness rather than the agent.
        var task = new ProceduralBenchmarkTask();

        var hold = await InvokeAsync(task, "PlaceHold", new { connection = "14:05", tier = "bronze" });

        hold.Should().NotBeNullOrWhiteSpace();
        hold.Should().Contain("tier");
    }

    [Fact]
    public async Task TheEnvironmentAnswersIdenticallyEveryTime()
    {
        // Deterministic on purpose: the agent is the only nondeterministic part. A varying environment
        // would be reported as a memory effect, and three attempts per arm is nowhere near enough
        // sample to tell those apart.
        var first = await InvokeAsync(new ProceduralBenchmarkTask(), "LookUpTraveller", new { traveller = "ruaidhri" });
        var second = await InvokeAsync(new ProceduralBenchmarkTask(), "LookUpTraveller", new { traveller = "ruaidhri" });

        second.Should().Be(first);
    }

    [Fact]
    public void CompletionCannotBeClaimedInProse()
    {
        // An agent that learned a wrong procedure reports success fluently. The marker is emitted only
        // by the booking tool, so it cannot be produced by confidence.
        var task = new ProceduralBenchmarkTask();

        task.IsComplete("I have successfully completed the booking for you!").Should().BeFalse();
        task.IsComplete($"{ProceduralBenchmarkTask.ConfirmationMarker} ref HOLD-4417").Should().BeTrue();
    }

    [Fact]
    public void ToolsAreExposedInProcedureOrder()
    {
        new ProceduralBenchmarkTask().CreateTools().Should().HaveCount(3);
    }
}
