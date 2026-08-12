using System.Globalization;
using System.Text;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Renders the per-memory-type table, with the caveats a reader needs attached to the numbers.
/// </summary>
/// <remarks>
/// <para>
/// The integrity rules this project already adopted are enforced <b>here</b>, in code, rather than
/// left to whoever writes the surrounding prose — because the failure mode being guarded against is a
/// number that travels without its context. A 57.7k-star project's headline died on exactly that.
/// </para>
/// <list type="bullet">
/// <item>Every figure carries its <b>question count</b> and what <b>one question is worth</b>.</item>
/// <item>A figure with no measured band is labelled as having none, never quoted bare.</item>
/// <item>An ablation inside the noise floor is printed as <b>no result</b>, not as a small win.</item>
/// <item>Types the dataset <b>cannot reach</b> are listed, so absence never reads as a zero.</item>
/// <item>The mapping revision is printed, because the grouping is an opinion.</item>
/// </list>
/// </remarks>
internal static class LongMemEvalTypedReport
{
    /// <summary>Markdown for a per-type breakdown, with bands when they have been measured.</summary>
    internal static string Render(
        IReadOnlyList<LongMemEvalTypedAccuracy> rows,
        IReadOnlyList<LongMemEvalTypedNoiseFloor>? noiseFloors = null,
        IReadOnlyList<LongMemEvalAblationResult>? ablations = null,
        string? mappingRevision = null)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var sb = new StringBuilder();
        var invariant = CultureInfo.InvariantCulture;

        sb.AppendLine("## Accuracy by memory type");
        sb.AppendLine();
        sb.AppendLine("| memory type | questions | accuracy | 1 question = | measured band | extraction-side | retrieval-side | unattributable |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");

        foreach (var row in rows)
        {
            var floor = noiseFloors?.FirstOrDefault(f => f.MemoryType == row.MemoryType);
            var band = floor is null || floor.Runs < 2
                ? "**not measured**"
                : string.Format(invariant, "±{0:F1} pts", floor.RangePoints / 2);

            sb.AppendLine(string.Format(
                invariant,
                "| {0} | {1} | {2} | {3:F1} pts | {4} | {5} | {6} | {7} |",
                row.MemoryType,
                row.Questions,
                row.Accuracy is { } a ? string.Format(invariant, "{0:P1}", a) : "—",
                row.PointsPerQuestion ?? 0,
                band,
                row.ExtractionFailures,
                row.RetrievalFailures,
                row.Unattributable));
        }

        sb.AppendLine();
        if (noiseFloors is null || noiseFloors.All(f => f.Runs < 2))
        {
            sb.AppendLine(
                "> ⚠️ **No band has been measured.** These figures come from a single run, so none of "
                + "them supports a comparison — with another configuration, or with anyone else's "
                + "published number. Repeats are what make a per-type figure quotable.");
            sb.AppendLine();
        }

        if (ablations is { Count: > 0 })
        {
            sb.AppendLine("## What each capability contributed");
            sb.AppendLine();
            sb.AppendLine("| capability | disabled via | gained | lost | net | verdict |");
            sb.AppendLine("|---|---|---:|---:|---:|---|");

            foreach (var ablation in ablations)
            {
                var survives = noiseFloors is not null
                    && LongMemEvalAblation.SurvivesNoiseFloor(ablation, noiseFloors);

                // Inside the band is a NULL RESULT and is printed as one. A component that does not
                // earn its tokens should be found here rather than by a competitor.
                var verdict = survives
                    ? string.Format(invariant, "**{0:+0.0;-0.0} pts**", ablation.NetPoints)
                    : "no result — inside the band";

                sb.AppendLine(string.Format(
                    invariant,
                    "| {0} | `{1}` | {2} | {3} | {4:+0;-0;0} | {5} |",
                    ablation.Capability.Name,
                    ablation.Capability.Option,
                    ablation.Gains.Count,
                    ablation.Losses.Count,
                    ablation.Net,
                    verdict));
            }

            sb.AppendLine();
            // Churn is invisible in a net figure and is the signature of a change moving noise.
            var churn = ablations.Where(a => a.Gains.Count > 0 && a.Losses.Count > 0).ToArray();
            if (churn.Length > 0)
            {
                sb.AppendLine(
                    "> **Churn:** " + string.Join(", ", churn.Select(a =>
                        $"`{a.Capability.Name}` gained {a.Gains.Count} and lost {a.Losses.Count}"))
                    + ". A capability that rescues and breaks in similar measure is moving answers "
                    + "around rather than adding signal, and the net alone would hide that.");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## What this dataset cannot measure");
        sb.AppendLine();
        sb.AppendLine(
            "**Procedural memory is unreachable here at any sample size.** LongMemEval-S is chat QA: "
            + "no build commands, no tool invocations, no fix trajectories. A score from this dataset "
            + "is not evidence about a procedural tier, and quoting one as though it were is the "
            + "metric substitution this report exists to avoid.");
        sb.AppendLine();
        sb.AppendLine(
            "**Meta-memory is reachable only through abstention questions**, so a run that sampled "
            + "none has not measured it — a missing row means unmeasured, never zero.");

        if (!string.IsNullOrWhiteSpace(mappingRevision))
        {
            sb.AppendLine();
            sb.AppendLine(
                $"_Task-label → memory-type mapping revision `{mappingRevision}`. The grouping is an "
                + "opinion; a figure computed from a different revision is a different figure._");
        }

        return sb.ToString();
    }
}
