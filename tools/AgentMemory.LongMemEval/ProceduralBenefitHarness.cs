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

    /// <summary>Whether the enabled arm's last attempt took fewer agent turns than its first.</summary>
    public bool ImprovedStepsWithRepetition => LastVersusFirst((first, last) => last.Steps < first.Steps);

    /// <summary>Whether the enabled arm's last attempt made fewer tool calls than its first.</summary>
    public bool ImprovedToolCallsWithRepetition =>
        LastVersusFirst((first, last) => last.ToolCalls < first.ToolCalls);

    /// <summary>
    /// Whether the enabled arm got cheaper <i>across attempts</i> rather than starting cheaper.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The learning signal. If the first attempt is already as cheap as the last, whatever separates
    /// the arms is not a procedure that was learned during the run.
    /// </para>
    /// <para>
    /// <b>Cheaper on either measure, worse on neither.</b> This previously read <c>Steps</c> alone, which
    /// made it blind to the saving this instrument is most likely to see: skipping one wasted tool call
    /// costs the agent the same number of turns, so an arm that learned to avoid a refusal scored as
    /// having learned nothing while <see cref="ToolCallReduction"/> — computed and reported by this same
    /// class — showed the saving. <b>Stated plainly because it matters for how the result is read: this
    /// rule was widened after a measured run produced exactly that shape.</b> The two per-measure flags
    /// are exposed separately so a reader can see which measure moved instead of taking the composite on
    /// trust.
    /// </para>
    /// <para>
    /// The "worse on neither" half is what keeps it from being a free pass: an arm that traded four extra
    /// turns for one fewer tool call has not learned a cheaper route, it has moved the cost.
    /// </para>
    /// </remarks>
    public bool ImprovedWithRepetition =>
        (ImprovedStepsWithRepetition || ImprovedToolCallsWithRepetition)
        && LastVersusFirst((first, last) => last.Steps <= first.Steps && last.ToolCalls <= first.ToolCalls);

    /// <summary>
    /// Applies a first-versus-last comparison over the enabled arm, requiring both attempts to have
    /// completed — an abandoned attempt is cheap for the wrong reason.
    /// </summary>
    private bool LastVersusFirst(Func<AgentTaskRun, AgentTaskRun, bool> compare) =>
        WithProcedures.FirstRun is { } first && WithProcedures.LastRun is { } last
        && first.Completed && last.Completed && compare(first, last);

    /// <summary>
    /// Run-to-run variation in the <b>control</b> arm's step count: the noise floor a gain must clear.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The control arm is the right place to measure noise because it cannot learn by construction —
    /// same agent, same task, no procedure store — so whatever spread it shows across attempts is the
    /// instrument's own jitter. At three attempts against a live model that jitter is roughly one step,
    /// which is the same size as the differences this harness was reporting as benefits.
    /// </para>
    /// <para>
    /// Deliberately <b>not</b> the enabled arm's spread. That arm is expected to be expensive on attempt
    /// one and cheaper afterwards, so learning inflates its standard deviation — using it as the floor
    /// would penalise precisely the shape the measurement is looking for.
    /// </para>
    /// </remarks>
    public double StepNoiseBand => Spread(WithoutProcedures, run => run.Steps);

    /// <summary>Run-to-run variation in the control arm's tool-call count.</summary>
    public double ToolCallNoiseBand => Spread(WithoutProcedures, run => run.ToolCalls);

    /// <summary>Whether the step saving is larger than the control arm's own run-to-run variation.</summary>
    public bool StepGainExceedsNoise =>
        WithoutProcedures.MeanStepsWhenCompleted - WithProcedures.MeanStepsWhenCompleted > StepNoiseBand;

    /// <summary>Whether the tool-call saving is larger than the control arm's own variation.</summary>
    public bool ToolCallGainExceedsNoise =>
        WithoutProcedures.MeanToolCallsWhenCompleted - WithProcedures.MeanToolCallsWhenCompleted
        > ToolCallNoiseBand;

    /// <summary>
    /// Whether the result may be reported as a benefit at all.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Correctness first, and not by a hair.</b> Any drop in completion rate disqualifies the
    /// efficiency claim outright: an agent with the wrong procedure executes confidently, and the
    /// steps it saves are not a saving if the task is not done. Cheapness is only a benefit on top of
    /// finishing at least as often.
    /// </para>
    /// <para>
    /// <b>Both documented comparisons are now required, not one.</b> This class has always said that
    /// "an arm that is faster from attempt one learned nothing", while this predicate ignored
    /// <see cref="ImprovedWithRepetition"/> entirely — so a run could report a benefit and, two lines
    /// below, report that nothing was learned. A measured run did exactly that.
    /// </para>
    /// <para>
    /// <b>And the saving has to clear the instrument's own noise.</b> Without a floor, a mean difference
    /// of a third of a step across three attempts scores as a benefit; that is the difference between
    /// two runs of the <i>same</i> configuration. See <see cref="StepNoiseBand"/>.
    /// </para>
    /// </remarks>
    public bool ShowsBenefit =>
        CompletionRateDelta >= 0
        && ImprovedWithRepetition
        && (StepGainExceedsNoise || ToolCallGainExceedsNoise);

    private static double Reduction(double baseline, double measured) =>
        baseline <= 0 ? 0d : (baseline - measured) / baseline;

    /// <summary>
    /// Sample standard deviation over an arm's completed runs. Zero for fewer than two of them — a
    /// single observation has no spread, and inventing one would either mask a real gain or manufacture
    /// one.
    /// </summary>
    private static double Spread(ProceduralBenefitArm arm, Func<AgentTaskRun, int> measure)
    {
        var values = arm.Runs.Where(run => run.Completed).Select(run => (double)measure(run)).ToList();
        if (values.Count < 2) return 0d;

        var mean = values.Average();
        return Math.Sqrt(values.Sum(value => (value - mean) * (value - mean)) / (values.Count - 1));
    }

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
