using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// Says how long a promoted procedure is, so an exploration-shaped one is visibly one.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measured failure this addresses.</b> Promotion captured the wrong thing: replaying the
/// archive task produced a procedure of <b>16 tool calls</b> — the agent's whole exploration, dead
/// ends included — and the benefit harness reported no benefit. Rendered as a bare outcome, that is
/// indistinguishable from a tight five-step recipe, so the model is handed sixteen steps of someone
/// else's flailing as if it were a method.
/// </para>
/// <para>
/// A length is the cheapest honest signal available without re-deriving the chain: distillation
/// (rewriting the outcome to the minimal contributing calls) is a separate, LLM-shaped proposal with
/// its own falsifier. This costs one integer and no call.
/// </para>
/// <para>
/// <b>Only procedures, never episodes.</b> An episode's length is not a claim about reusability, and
/// annotating one would spend tokens saying something true and useless.
/// </para>
/// </remarks>
internal sealed class ProcedureShapeProjectionFeature : IProjectionFeature
{
    /// <summary>
    /// Shares the match-quality flag deliberately: both exist to stop a procedure being trusted more
    /// than it has earned, and a second flag for one clause would be configuration surface without a
    /// separate decision behind it.
    /// </summary>
    public bool IsEnabled(MemoryProjectionOptions options) => options.AnnotateMatchQuality;

    public Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        foreach (var trace in state.Traces)
        {
            if (trace.Kind != TraceKind.Procedure) continue;

            var steps = CountSteps(trace.Outcome);
            if (steps < 2) continue;

            state.Annotate(trace.TraceId, annotation => annotation with
            {
                ProcedureShape = $"({steps} steps)",
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Counts the steps a recorded procedure describes, from the outcome text.
    /// </summary>
    /// <remarks>
    /// Newline-delimited, because that is how a promoted trace's outcome is written. Deliberately
    /// conservative: anything it cannot count confidently reports fewer than two steps and renders
    /// nothing, since an invented step count on a procedure would be worse than silence — it is
    /// precisely the over-trust this feature exists to prevent.
    /// </remarks>
    internal static int CountSteps(string? outcome)
    {
        if (string.IsNullOrWhiteSpace(outcome)) return 0;

        return outcome
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(line => line.Length > 0);
    }
}
