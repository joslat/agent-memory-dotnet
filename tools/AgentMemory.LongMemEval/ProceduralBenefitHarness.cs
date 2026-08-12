namespace AgentMemory.LongMemEval;

/// <summary>What one attempt at a task cost, and whether it worked (7.6).</summary>
/// <param name="Completed">Whether the task was actually finished. The gate on every other number.</param>
/// <param name="Steps">Agent turns taken.</param>
/// <param name="ToolCalls">Tool invocations made.</param>
public sealed record AgentTaskRun(bool Completed, int Steps, int ToolCalls);

/// <summary>
/// Runs a task once and reports what it cost (7.6).
/// </summary>
/// <remarks>
/// A seam so the harness can be exercised against a scripted agent in a unit test and against a real
/// Agent Framework agent in a measured run. The harness is the part that can be wrong in a way nobody
/// notices; the agent is the part that costs money.
/// </remarks>
public interface IAgentTaskRunner
{
    /// <summary>Attempts the task once, with procedural memory on or off.</summary>
    Task<AgentTaskRun> RunAsync(
        string taskId, bool procedureMemoryEnabled, int attempt, CancellationToken cancellationToken = default);
}

/// <summary>One arm of the comparison, aggregated (7.6).</summary>
public sealed record ProceduralBenefitArm(bool ProcedureMemoryEnabled, IReadOnlyList<AgentTaskRun> Runs)
{
    /// <summary>Share of attempts that finished the task.</summary>
    /// <remarks>
    /// <b>The gate on every efficiency number beside it.</b> An agent that gives up sooner takes fewer
    /// steps and makes fewer tool calls, and both of those read as an improvement.
    /// </remarks>
    public double CompletionRate => Runs.Count == 0 ? 0d : (double)Runs.Count(r => r.Completed) / Runs.Count;

    /// <summary>Mean steps over the runs that completed.</summary>
    /// <remarks>
    /// Averaged over <b>completed</b> runs only. Including failures mixes "solved it in three steps"
    /// with "gave up after three", and the second is not a cheaper success.
    /// </remarks>
    public double MeanStepsWhenCompleted => Mean(Runs.Where(r => r.Completed).Select(r => (double)r.Steps));

    /// <summary>Mean tool calls over the runs that completed.</summary>
    public double MeanToolCallsWhenCompleted =>
        Mean(Runs.Where(r => r.Completed).Select(r => (double)r.ToolCalls));

    /// <summary>The first attempt, before any procedure could have been learned.</summary>
    public AgentTaskRun? FirstRun => Runs.Count > 0 ? Runs[0] : null;

    /// <summary>The last attempt, by which point a procedure should exist.</summary>
    public AgentTaskRun? LastRun => Runs.Count > 0 ? Runs[^1] : null;

    private static double Mean(IEnumerable<double> values)
    {
        var list = values.ToList();
        return list.Count == 0 ? 0d : list.Average();
    }
}

/// <summary>
/// Whether procedural memory actually helps an agent repeat a task (7.6).
/// </summary>
/// <remarks>
/// <para>
/// <b>LongMemEval cannot answer this.</b> It scores answers to questions about a transcript; a
/// promoted procedure changes how an agent <i>works</i>, over repeated attempts at a multi-step task.
/// With no trace nodes and — until 6.5 — a probe that was label-blind to them, a promoted procedure
/// was invisible to every instrument this project had, which is to say the feature <b>could not
/// fail</b>. That is the defect shape this harness exists to prevent, not merely a gap in coverage.
/// </para>
/// <para>
/// Two comparisons, and both are needed. <b>On versus off</b> answers "does the feature help at all".
/// <b>First run versus last</b> answers "does it help because it learned, or because the two arms
/// differ for some other reason" — an arm that is faster from attempt one learned nothing.
/// </para>
/// <para>
/// Every efficiency number is gated on completion rate. An agent that abandons a task sooner takes
/// fewer steps and makes fewer tool calls, and a harness that reported those alone would score giving
/// up as the biggest improvement available.
/// </para>
/// </remarks>
public sealed record ProceduralBenefitResult
{
    /// <summary>The arm with procedural memory enabled.</summary>
    public required ProceduralBenefitArm WithProcedures { get; init; }

    /// <summary>The control arm.</summary>
    public required ProceduralBenefitArm WithoutProcedures { get; init; }

    /// <summary>Reduction in mean steps, as a fraction. Negative means it got worse.</summary>
    public double StepReduction => Reduction(
        WithoutProcedures.MeanStepsWhenCompleted, WithProcedures.MeanStepsWhenCompleted);

    /// <summary>Reduction in mean tool calls, as a fraction.</summary>
    public double ToolCallReduction => Reduction(
        WithoutProcedures.MeanToolCallsWhenCompleted, WithProcedures.MeanToolCallsWhenCompleted);

    /// <summary>Change in completion rate; negative means procedures cost correctness.</summary>
    public double CompletionRateDelta =>
        WithProcedures.CompletionRate - WithoutProcedures.CompletionRate;

    /// <summary>
    /// Whether the enabled arm got cheaper <i>across attempts</i> rather than starting cheaper.
    /// </summary>
    /// <remarks>
    /// The learning signal. If the first attempt is already as cheap as the last, whatever separates
    /// the arms is not a procedure that was learned during the run.
    /// </remarks>
    public bool ImprovedWithRepetition =>
        WithProcedures.FirstRun is { } first && WithProcedures.LastRun is { } last
        && last.Completed && first.Completed && last.Steps < first.Steps;

    /// <summary>
    /// Whether the result may be reported as a benefit at all.
    /// </summary>
    /// <remarks>
    /// <b>Correctness first, and not by a hair.</b> Any drop in completion rate disqualifies the
    /// efficiency claim outright: an agent with the wrong procedure executes confidently, and the
    /// steps it saves are not a saving if the task is not done. Cheapness is only a benefit on top of
    /// finishing at least as often.
    /// </remarks>
    public bool ShowsBenefit =>
        CompletionRateDelta >= 0 && (StepReduction > 0 || ToolCallReduction > 0);

    private static double Reduction(double baseline, double measured) =>
        baseline <= 0 ? 0d : (baseline - measured) / baseline;

    /// <summary>
    /// Runs both arms and compares them (7.6).
    /// </summary>
    /// <param name="runner">Executes one attempt.</param>
    /// <param name="taskId">The repeated task.</param>
    /// <param name="attempts">How many times to repeat it per arm.</param>
    public static async Task<ProceduralBenefitResult> MeasureAsync(
        IAgentTaskRunner runner,
        string taskId,
        int attempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 2);

        return new ProceduralBenefitResult
        {
            WithProcedures = await RunArmAsync(runner, taskId, true, attempts, cancellationToken)
                .ConfigureAwait(false),
            WithoutProcedures = await RunArmAsync(runner, taskId, false, attempts, cancellationToken)
                .ConfigureAwait(false),
        };
    }

    private static async Task<ProceduralBenefitArm> RunArmAsync(
        IAgentTaskRunner runner, string taskId, bool enabled, int attempts, CancellationToken cancellationToken)
    {
        var runs = new List<AgentTaskRun>(attempts);
        // Sequential, deliberately. The whole hypothesis is that attempt N benefits from what attempt
        // N-1 stored, and running them concurrently would race the write that the next read depends on
        // -- measuring a feature that had not happened yet.
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            runs.Add(await runner.RunAsync(taskId, enabled, attempt, cancellationToken).ConfigureAwait(false));
        }

        return new ProceduralBenefitArm(enabled, runs);
    }
}
