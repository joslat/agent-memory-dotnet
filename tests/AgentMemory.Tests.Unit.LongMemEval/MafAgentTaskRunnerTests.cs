using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Counting what an agent actually did (7.6).
/// </summary>
/// <remarks>
/// <para>
/// The harness decides what a benefit <i>is</i>; this decides what the numbers <i>are</i>. Both are
/// read off the returned messages rather than from the agent's own account of itself — asking a model
/// how many tools it used yields a figure that tracks how talkative it is, and a procedure that made
/// the agent merely more confident would show up as an efficiency win.
/// </para>
/// <para>
/// Provider-free: these assert the counting rules against constructed transcripts, which is the part
/// that can be quietly wrong. The part that costs money is the run.
/// </para>
/// </remarks>
public sealed class MafAgentTaskRunnerTests
{
    private static ChatMessage Assistant(params AIContent[] contents) =>
        new(ChatRole.Assistant, [.. contents]);

    private static FunctionCallContent Call(string id, string name) => new(id, name, null);

    [Fact]
    public void StepsCountAssistantTurns()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "do the task"),
            Assistant(new TextContent("thinking")),
            Assistant(new TextContent("done")),
        };

        MafAgentTaskRunner.CountSteps(messages).Should().Be(2);
    }

    [Fact]
    public void ToolResultsAreNotSteps()
    {
        // The environment answering is not the agent acting. Counting tool results would make a
        // procedure that batches its calls look like it took MORE steps rather than fewer -- inverting
        // the exact signal the harness is looking for.
        var messages = new[]
        {
            Assistant(Call("c1", "search")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "result")]),
            Assistant(new TextContent("done")),
        };

        MafAgentTaskRunner.CountSteps(messages).Should().Be(2);
    }

    [Fact]
    public void ToolCallsAreCountedAcrossEveryMessage()
    {
        // Several calls can share one assistant turn -- that is what batching looks like, and counting
        // per message instead of per call would hide it.
        var messages = new[]
        {
            Assistant(Call("c1", "search"), Call("c2", "lookup")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "a")]),
            Assistant(Call("c3", "book")),
        };

        MafAgentTaskRunner.CountToolCalls(messages).Should().Be(3);
    }

    [Fact]
    public void AToolResultIsNotAToolCall()
    {
        var messages = new[]
        {
            Assistant(Call("c1", "search")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "result")]),
        };

        MafAgentTaskRunner.CountToolCalls(messages).Should().Be(1);
    }

    [Fact]
    public void AnEmptyTranscriptCountsZeroRatherThanThrowing()
    {
        // A run that failed before producing anything must score as an expensive nothing, not crash
        // the arm and take the other attempts' data with it.
        MafAgentTaskRunner.CountSteps([]).Should().Be(0);
        MafAgentTaskRunner.CountToolCalls([]).Should().Be(0);
    }

    [Fact]
    public void CompletionIsDecidedByTheCallerNotTheAgent()
    {
        // An agent that learned a WRONG procedure reports success fluently. Completion has to be
        // checked against the world, which is why the predicate is supplied rather than inferred from
        // the agent claiming it finished -- and why 7.7 measures wrong-procedure rate separately.
        Func<string, bool> strict = text => text.Contains("BOOKING-CONFIRMED", StringComparison.Ordinal);

        strict("I have completed the task successfully!").Should().BeFalse();
        strict("BOOKING-CONFIRMED ref 91821").Should().BeTrue();
    }
}
