using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using NSubstitute;
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
    public void TheProcedureChainExcludesRefusedCalls()
    {
        // A transcript is not a procedure. The raw sequence is how the agent DISCOVERED the route, and
        // replaying it repeats the wasted call -- which is what tied the arms at six tool calls on the
        // seventh run.
        var messages = new[]
        {
            Assistant(Call("c1", "PlaceHold")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "refused: session is stale")]),
            Assistant(Call("c2", "RefreshSession")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c2", "session refreshed")]),
            Assistant(Call("c3", "PlaceHold")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c3", "hold placed; reference=H1")]),
        };

        MafAgentTaskRunner.ProcedureChain(messages, ProceduralBenchmarkTask.IsRefusal)
            .Should().Equal("RefreshSession", "PlaceHold");
    }

    [Fact]
    public void RefusedCallsAreStillCountedAsToolCalls()
    {
        // Filtering changes what is LEARNED, never what was CHARGED. The agent really did spend the
        // refused call, and discounting it would make the instrument flatter the feature.
        var messages = new[]
        {
            Assistant(Call("c1", "PlaceHold")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "refused: session is stale")]),
            Assistant(Call("c2", "RefreshSession")),
        };

        MafAgentTaskRunner.CountToolCalls(messages).Should().Be(2);
    }

    [Fact]
    public void WithoutARefusalPredicateEveryCallIsRecorded()
    {
        // No silent filtering for a caller that never opted in: a task whose refusals are worded
        // differently must not have its procedures quietly trimmed by a guess made here.
        var messages = new[]
        {
            Assistant(Call("c1", "PlaceHold")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "refused: session is stale")]),
            Assistant(Call("c2", "RefreshSession")),
        };

        MafAgentTaskRunner.ProcedureChain(messages, isRefusal: null)
            .Should().Equal("PlaceHold", "RefreshSession");
    }

    [Fact]
    public void RefusalsArePairedByCallIdNotByPosition()
    {
        // A batched assistant turn issues several calls whose results arrive separately. Matching by
        // order would attribute one call's refusal to another and store a procedure missing a step that
        // worked.
        var messages = new[]
        {
            Assistant(Call("c1", "Alpha"), Call("c2", "Beta")),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c2", "refused: not yet")]),
            new ChatMessage(ChatRole.Tool, [new FunctionResultContent("c1", "alpha done")]),
        };

        MafAgentTaskRunner.ProcedureChain(messages, ProceduralBenchmarkTask.IsRefusal)
            .Should().Equal("Alpha");
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

/// <summary>
/// Promoting a successful attempt to a reusable procedure (7.6).
/// </summary>
/// <remarks>
/// <para>
/// Without this the benchmark measures nothing. The Agent Framework provider recalls reasoning traces
/// and <b>never writes one</b> — <c>AgentTraceRecorder</c> is gated off by default and is not called
/// on a normal run — so the procedural arm would recall procedures while nothing stored any. Both arms
/// would behave identically and the harness would report "no benefit" while describing a wiring gap.
/// </para>
/// <para>
/// Asserted against the promotion <i>decision</i> rather than through a stub agent: the decision is
/// what can be quietly wrong, and routing through an agent would test the plumbing around it instead.
/// </para>
/// </remarks>
public sealed class ProcedurePromotionTests
{
    /// <summary>Substituted rather than hand-written: only AddAsync is under test.</summary>
    private static (IReasoningTraceRepository Repo, List<ReasoningTrace> Added) Traces()
    {
        var added = new List<ReasoningTrace>();
        var repo = Substitute.For<IReasoningTraceRepository>();
        repo.AddAsync(Arg.Any<ReasoningTrace>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var trace = call.Arg<ReasoningTrace>();
                added.Add(trace);
                return Task.FromResult(trace);
            });
        return (repo, added);
    }

    private static readonly string[] Chain = ["LookUpTraveller", "PlaceHold", "Book"];

    private static readonly float[] Vector = [0.1f, 0.2f, 0.3f];

    /// <summary>An orchestrator that embeds successfully, which is the ordinary case.</summary>
    private static IEmbeddingOrchestrator Embeddings(float[]? vector = null)
    {
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(vector ?? Vector));
        return embeddings;
    }

    private static MafAgentTaskRunner Runner(
        IReasoningTraceRepository? traces, IEmbeddingOrchestrator? embeddings = null) =>
        new(_ => null!, "book it", _ => true, traces, embeddings ?? Embeddings());

    private static Task PromoteAsync(
        IReasoningTraceRepository traces, bool completed, bool enabled,
        IEmbeddingOrchestrator? embeddings = null) =>
        Runner(traces, embeddings).PromoteIfWorthKeepingAsync(
            new AgentTaskRun(completed, Steps: 3, ToolCalls: 3), enabled, Chain);

    [Fact]
    public async Task ASuccessfulProceduralAttemptIsPromoted()
    {
        // THE gap this closes. Nothing else in the MAF path writes a trace, so without this the
        // procedural arm has nothing to recall on attempt two.
        var (repo, added) = Traces();

        await PromoteAsync(repo, completed: true, enabled: true);

        added.Should().ContainSingle();
    }

    [Fact]
    public async Task ThePromotedTraceIsAProcedureNotAnEpisode()
    {
        // The kind is what proceduresOnly recall and the retention exemption key on. (It is NOT what the
        // MAF provider's automatic recall filters on -- that path passes no kind filter at all. An
        // earlier version of this comment claimed it did, which was wrong and would have sent anyone
        // debugging an empty procedural arm to the wrong place.)
        var (repo, added) = Traces();

        await PromoteAsync(repo, completed: true, enabled: true);

        added.Single().Kind.Should().Be(TraceKind.Procedure);
    }

    [Fact]
    public async Task ThePromotedTraceCarriesATaskEmbedding()
    {
        // THE gap that would have voided the seventh run. Trace recall is a vector search -- the indexed
        // path and the owner-scoped fallback both require task_embedding IS NOT NULL -- so a procedure
        // stored without one is persisted, counted as promoted, and returned by nothing.
        var (repo, added) = Traces();

        await PromoteAsync(repo, completed: true, enabled: true);

        added.Single().TaskEmbedding.Should().BeEquivalentTo(Vector);
    }

    [Fact]
    public async Task PromotionWithoutAnEmbedderThrowsRatherThanStoringAnUnreachableProcedure()
    {
        // Failing loudly is the whole point: a silently unreachable procedure reads as an honest
        // negative result, which is how five earlier runs came to measure nothing.
        var (repo, added) = Traces();
        var runner = new MafAgentTaskRunner(_ => null!, "book it", _ => true, repo, embeddings: null);

        var act = async () => await runner.PromoteIfWorthKeepingAsync(
            new AgentTaskRun(true, 3, 3), procedureMemoryEnabled: true, Chain);

        await act.Should().ThrowAsync<InvalidOperationException>();
        added.Should().BeEmpty();
    }

    [Fact]
    public async Task AnEmptyEmbeddingVectorThrowsRatherThanStoringAnUnreachableProcedure()
    {
        // EmbedAsync returns an EMPTY array on failure rather than throwing, so this is the quiet
        // version of the same defect: promotion appears to succeed and the arm recalls nothing.
        var (repo, added) = Traces();

        var act = async () => await PromoteAsync(
            repo, completed: true, enabled: true, embeddings: Embeddings([]));

        await act.Should().ThrowAsync<InvalidOperationException>();
        added.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailedAttemptIsNotPromoted()
    {
        // Promoting a failure teaches the wrong chain. 7.7's wrong-procedure rate would then correctly
        // punish it, and the benchmark would be measuring a bug introduced here rather than the feature.
        var (repo, added) = Traces();

        await PromoteAsync(repo, completed: false, enabled: true);

        added.Should().BeEmpty();
    }

    [Fact]
    public async Task TheControlArmNeverPromotes()
    {
        // The arm switch. If the control arm promoted too, both arms would share a procedure store and
        // the comparison would measure nothing at all.
        var (repo, added) = Traces();

        await PromoteAsync(repo, completed: true, enabled: false);

        added.Should().BeEmpty();
    }

    [Fact]
    public async Task TheProcedureRecordsTheToolChain()
    {
        // What makes it reusable rather than a note that something worked once. Worded to survive the
        // HTML escaping every recalled block goes through (#92 Phase 1) -- an arrow-joined chain reaches
        // the model as "a -&gt; b", which is the procedure arriving damaged rather than not arriving.
        var (repo, added) = Traces();

        await PromoteAsync(repo, completed: true, enabled: true);

        added.Single().Outcome.Should().Be("LookUpTraveller then PlaceHold then Book");
    }

    [Fact]
    public async Task NoRepositoryMeansNoPromotionAndNoCrash()
    {
        var act = async () => await Runner(null).PromoteIfWorthKeepingAsync(
            new AgentTaskRun(true, 3, 3), procedureMemoryEnabled: true, Chain);

        await act.Should().NotThrowAsync();
    }
}
