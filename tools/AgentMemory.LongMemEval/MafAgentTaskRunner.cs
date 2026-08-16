using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
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
    private readonly IEmbeddingOrchestrator? _embeddings;
    private readonly Func<string, bool>? _isRefusal;
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
    /// <param name="embeddings">
    /// Embeds the promoted procedure's task text. Required whenever <paramref name="traces"/> is
    /// supplied: trace recall is a vector search, so a procedure stored without one is unreachable —
    /// see <see cref="PromoteIfWorthKeepingAsync"/>.
    /// </param>
    /// <param name="isRefusal">
    /// Decides whether a tool result means the call was declined, so a promoted procedure records the
    /// calls that worked instead of the transcript of how they were found — see
    /// <see cref="ProcedureChain"/>. <see langword="null"/> records every call, the previous behaviour.
    /// </param>
    /// <param name="ownerId">Owner the procedure is stored under; recall is owner-scoped.</param>
    public MafAgentTaskRunner(
        Func<bool, AIAgent> agentFactory,
        string taskPrompt,
        Func<string, bool> isComplete,
        IReasoningTraceRepository? traces = null,
        IEmbeddingOrchestrator? embeddings = null,
        Func<string, bool>? isRefusal = null,
        string ownerId = "procedural-benchmark")
    {
        _agentFactory = agentFactory;
        _taskPrompt = taskPrompt;
        _isComplete = isComplete;
        _traces = traces;
        _embeddings = embeddings;
        _isRefusal = isRefusal;
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
        //
        // The owner stamp is load-bearing, not bookkeeping: recall derives its MemoryScope from this
        // userId, and the procedures are promoted under _ownerId. Leave it off and the arm recalls
        // across every owner or none, and either way not the thing it stored. Both arms are stamped
        // identically -- the control has no provider to read it -- so this is not an arm difference.
        var session = (await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false))
            .WithMemoryIdentity(
                userId: _ownerId,
                sessionId: $"{_ownerId}-attempt-{attempt}-{(procedureMemoryEnabled ? "proc" : "ctl")}");

        var response = await agent.RunAsync(_taskPrompt, session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var messages = response.Messages ?? [];
        var run = new AgentTaskRun(
            Completed: _isComplete(response.Text ?? string.Empty),
            Steps: CountSteps(messages),
            ToolCalls: CountToolCalls(messages));

        await PromoteIfWorthKeepingAsync(
                run, procedureMemoryEnabled, ProcedureChain(messages, _isRefusal), cancellationToken)
            .ConfigureAwait(false);
        return run;
    }

    /// <summary>
    /// The calls a promoted procedure should record: those that did work, in order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A transcript is not a procedure.</b> The raw call sequence is how the agent <i>discovered</i>
    /// the route, refused calls and all, and a stored procedure that replays it makes the same wasted
    /// call the second time. That is not a subtle effect: the seventh run promoted
    /// "PlaceHold then RefreshSession then PlaceHold", the arm holding it still spent six tool calls, and
    /// the two arms tied — a null result caused by what promotion recorded rather than by the feature.
    /// </para>
    /// <para>
    /// Refusal is decided by a caller-supplied predicate, exactly as completion is. The runner cannot
    /// know what "the environment declined" looks like, and a heuristic guess here would silently
    /// mis-record procedures for any task whose refusals are worded differently. With no predicate the
    /// behaviour is unchanged — every call is recorded — so nothing is quietly filtered from a caller
    /// that never opted in.
    /// </para>
    /// <para>
    /// Counting is untouched: <see cref="CountToolCalls"/> still counts the refused call, because the
    /// agent really did spend it. Filtering here changes what is <i>learned</i>, never what is
    /// <i>charged</i> — the opposite mistake would make the instrument flatter the feature.
    /// </para>
    /// </remarks>
    internal static IEnumerable<string> ProcedureChain(
        IEnumerable<ChatMessage> messages, Func<string, bool>? isRefusal)
    {
        var all = messages.ToList();
        var calls = all.SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
        if (isRefusal is null) return calls.Select(call => call.Name).ToList();

        // Paired by CallId rather than by position: a batched assistant turn issues several calls whose
        // results arrive in their own messages, and matching by order would attribute one call's refusal
        // to another.
        var refusedCallIds = all
            .SelectMany(m => m.Contents.OfType<FunctionResultContent>())
            .Where(result => isRefusal(result.Result?.ToString() ?? string.Empty))
            .Select(result => result.CallId)
            .ToHashSet(StringComparer.Ordinal);

        return calls
            .Where(call => !refusedCallIds.Contains(call.CallId))
            .Select(call => call.Name)
            .ToList();
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
    /// <b>Written as <see cref="TraceKind.Procedure"/>, never Episode.</b> The kind is what
    /// <c>proceduresOnly</c> recall and the retention exemption both key on, so an episode-kinded trace
    /// is a procedure only by intention. (It is <i>not</i> filtered out by the MAF provider's automatic
    /// recall, which passes no kind filter at all — an earlier note here claimed it was, and that claim
    /// was wrong.)
    /// </para>
    /// <para>
    /// <b>Stored with a task embedding, or not at all.</b> Trace recall is a vector search over
    /// <c>task_embedding</c> — on the indexed path and on the owner-scoped fallback alike, which
    /// requires <c>task_embedding IS NOT NULL</c>. A trace written without one is persisted,
    /// counts as promoted, and is returned by no search that exists: the procedural arm would find
    /// nothing while the store filled up with procedures. That is the same false negative as a missing
    /// promotion, one layer down, so this throws rather than storing an unreachable trace.
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

        if (_embeddings is null)
            throw new InvalidOperationException(
                "Promotion needs an IEmbeddingOrchestrator: trace recall is a vector search, so a "
                + "procedure stored without a task embedding is unreachable and the arm silently "
                + "measures nothing.");

        // EmbedAsync returns an EMPTY array on a blank input or a generation failure rather than
        // throwing, so the failure mode this guards is the quiet one: promotion appears to succeed and
        // the procedure is invisible for the rest of the run.
        var taskEmbedding = await _embeddings.EmbedAsync(_taskPrompt, cancellationToken)
            .ConfigureAwait(false);
        if (taskEmbedding.Length == 0)
            throw new InvalidOperationException(
                "Embedding the procedure's task text returned an empty vector; the promoted procedure "
                + "would be unreachable by trace recall. Aborting rather than recording a null result.");

        // Joined with " then " rather than an arrow: recalled memory is HTML-escaped before it reaches
        // the model (#92 Phase 1), so a "->" chain arrives as "a -&gt; b -&gt; c". The escaping is the
        // security property and stays; the procedure is written so it survives it legibly.
        var chain = string.Join(" then ", toolNames);

        await _traces.AddAsync(
            new ReasoningTrace
            {
                TraceId = $"proc-{Guid.NewGuid():N}",
                SessionId = "procedural-benchmark",
                Task = _taskPrompt,
                TaskEmbedding = taskEmbedding,
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
