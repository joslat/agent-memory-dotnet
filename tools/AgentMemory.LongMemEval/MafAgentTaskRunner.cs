using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Drives a repeated multi-step task through a real Agent Framework agent and reports what it cost
/// (7.6).
/// </summary>
/// <remarks>
/// <para>
/// The half of the procedural-benefit harness that cannot be scripted. The harness decides what a
/// benefit <i>is</i> — completion first, efficiency second, learning proved across attempts — and is
/// unit-tested against a scripted runner. This is the part that actually calls an agent, so it is
/// where an honest step and tool-call count has to come from.
/// </para>
/// <para>
/// <b>Steps and tool calls are counted from the run, never from the agent's own account of itself.</b>
/// Asking a model how many tools it used produces a number that correlates with how talkative it is.
/// Both figures here are read off the returned messages, which is what makes them a measurement
/// rather than a self-report — and which is what would otherwise quietly flatter a procedure that
/// merely made the agent more confident.
/// </para>
/// <para>
/// Completion is decided by a caller-supplied predicate over the transcript, not by the agent saying
/// it is done. An agent that has learned a wrong procedure will report success fluently, and 7.7's
/// wrong-procedure rate exists precisely because that failure is invisible to efficiency numbers.
/// </para>
/// </remarks>
internal sealed class MafAgentTaskRunner : IAgentTaskRunner
{
    private readonly Func<bool, AIAgent> _agentFactory;
    private readonly string _taskPrompt;
    private readonly Func<string, bool> _isComplete;
    private readonly IReasoningTraceRepository? _traces;
    private readonly string _ownerId;

    /// <param name="agentFactory">
    /// Builds an agent with procedural memory on or off. A factory rather than two instances because
    /// the arms must not share conversation state — a second run reusing the first arm's thread would
    /// measure the thread, not the memory.
    /// </param>
    /// <param name="taskPrompt">The task, issued identically on every attempt.</param>
    /// <param name="isComplete">Decides completion from the final transcript.</param>
    /// <param name="traces">
    /// Where a successful attempt is promoted to a procedure. <see langword="null"/> disables
    /// promotion, which is what the control arm uses.
    /// </param>
    /// <param name="ownerId">Owner the procedure is stored under; recall is owner-scoped.</param>
    public MafAgentTaskRunner(
        Func<bool, AIAgent> agentFactory,
        string taskPrompt,
        Func<string, bool> isComplete,
        IReasoningTraceRepository? traces = null,
        string ownerId = "procedural-benchmark")
    {
        _agentFactory = agentFactory;
        _taskPrompt = taskPrompt;
        _isComplete = isComplete;
        _traces = traces;
        _ownerId = ownerId;
    }

    /// <inheritdoc/>
    public async Task<AgentTaskRun> RunAsync(
        string taskId,
        bool procedureMemoryEnabled,
        int attempt,
        CancellationToken cancellationToken = default)
    {
        var agent = _agentFactory(procedureMemoryEnabled);

        // A fresh session per attempt. Procedural memory is supposed to carry across attempts through
        // the STORE; a shared session would carry it through the context window instead, and the
        // measurement would credit memory for what the transcript did.
        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);

        var response = await agent.RunAsync(_taskPrompt, session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var messages = response.Messages ?? [];
        var run = new AgentTaskRun(
            Completed: _isComplete(response.Text ?? string.Empty),
            Steps: CountSteps(messages),
            ToolCalls: CountToolCalls(messages));

        await PromoteIfWorthKeepingAsync(
                run,
                procedureMemoryEnabled,
                messages.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).Select(c => c.Name),
                cancellationToken)
            .ConfigureAwait(false);
        return run;
    }

    /// <summary>Assistant turns taken — the agent's own reasoning steps.</summary>
    /// <remarks>
    /// Tool-result messages are excluded: they are the environment answering, not the agent acting,
    /// and counting them would make a procedure that batches tool calls look like it took more steps
    /// rather than fewer.
    /// </remarks>
    internal static int CountSteps(IEnumerable<ChatMessage> messages) =>
        messages.Count(m => m.Role == ChatRole.Assistant);

    /// <summary>Tool invocations across the whole run.</summary>
    internal static int CountToolCalls(IEnumerable<ChatMessage> messages) =>
        messages.Sum(m => m.Contents.OfType<FunctionCallContent>().Count());

    /// <summary>
    /// Stores a successful attempt as a reusable procedure (7.6).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Without this the benchmark measures nothing.</b> The Agent Framework provider recalls
    /// reasoning traces and never writes one — <c>AgentTraceRecorder</c> is gated off by default and
    /// is not called on a normal run — so the procedural arm would recall procedures while nothing
    /// stored any, both arms would behave identically, and the harness would report "no benefit"
    /// while actually describing a wiring gap.
    /// </para>
    /// <para>
    /// <b>Successful attempts only.</b> Promoting a failed one teaches the wrong chain; 7.7's
    /// wrong-procedure rate would then correctly punish it, and the benchmark would be measuring a
    /// bug introduced here rather than the feature.
    /// </para>
    /// <para>
    /// <b>Written as <see cref="TraceKind.Procedure"/>, never Episode.</b> Owner-scoped procedural
    /// recall filters on procedures, so an episode-kinded trace is stored, recalled by nothing, and
    /// presents as exactly the same false negative this method exists to prevent.
    /// </para>
    /// </remarks>
    internal async Task PromoteIfWorthKeepingAsync(
        AgentTaskRun run,
        bool procedureMemoryEnabled,
        IEnumerable<string> toolNames,
        CancellationToken cancellationToken = default)
    {
        // The arm switch. The control arm passes no repository and therefore never promotes, which is
        // what makes the two arms differ in the feature rather than in their prompts.
        if (_traces is null || !procedureMemoryEnabled || !run.Completed) return;

        var chain = string.Join(" -> ", toolNames);

        await _traces.AddAsync(
            new ReasoningTrace
            {
                TraceId = $"proc-{Guid.NewGuid():N}",
                SessionId = "procedural-benchmark",
                Task = _taskPrompt,
                Outcome = chain,
                Success = true,
                Kind = TraceKind.Procedure,
                OwnerId = _ownerId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            },
            cancellationToken).ConfigureAwait(false);
    }
}
