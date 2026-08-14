using System.Text.Json;

namespace AgentMemory.LongMemEval;

/// <summary>
/// 25.7. Renders the per-memory-type breakdown from reports already on disk, with a <b>measured</b>
/// noise band when the same arm was run more than once.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this verb exists.</b> Three tested types — <see cref="LongMemEvalTypedBreakdown"/>,
/// <see cref="LongMemEvalTypedNoiseFloorCalculator"/> and <see cref="LongMemEvalTypedReport"/> — formed
/// a complete per-type reporting stack that <b>nothing in production called</b>. Every per-type figure
/// this project has published was recomputed by hand from raw artifacts, and the results write-up
/// described the per-type noise floor as "a separate and larger piece of work" while a calculator for
/// it sat finished and unreachable.
/// </para>
/// <para>
/// <b>It costs nothing to run.</b> Purely retrospective: it reads existing report JSON, makes no
/// provider call, needs no Neo4j and no corpus. That is also why it should never have been left
/// unwired — there was no budget reason not to.
/// </para>
/// <para>
/// <b>The band is measured, not assumed.</b> Given several reports for the same arm it reports the
/// observed spread per type. Given one, it reports the band as unmeasured rather than zero — a single
/// run has no spread, and printing 0.0 would claim a precision no one has established.
/// </para>
/// </remarks>
internal static class LongMemEvalTypedReportProgram
{
    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            var reports = ResolveReports(args);
            if (reports.Count == 0)
            {
                Console.Error.WriteLine(
                    "longmemeval: no reports found. Pass --reports <dir-or-file,...> or run from a "
                    + "workspace with artifacts/evaluation/.");
                return Task.FromResult(2);
            }

            var arm = Value(args, "--arm") ?? "structured";
            List<IReadOnlyList<LongMemEvalTypedAccuracy>> runs = [];
            List<string> used = [];

            foreach (var path in reports)
            {
                var rows = ReadArm(path, arm);
                if (rows is null || rows.Count == 0) continue;
                runs.Add(rows);
                used.Add(Path.GetFileName(Path.GetDirectoryName(path)) ?? Path.GetFileName(path));
            }

            if (runs.Count == 0)
            {
                Console.Error.WriteLine(
                    $"longmemeval: no report contained a scorable '{arm}' arm. "
                    + "Reports need per-question judgments and question types.");
                return Task.FromResult(2);
            }

            // Runs of different SIZES are not repeats of one another. Pooling a 2-question smoke run
            // with a 50-question measurement produces a "band" that is mostly the difference between
            // sample sizes -- and the noise-floor calculator's own contract requires repeats of the
            // same arm AND configuration. So the largest cohort wins and everything else is named as
            // excluded rather than silently averaged in.
            var cohorts = runs
                .Select((rows, index) => (rows, index, size: rows.Sum(row => row.Questions)))
                .GroupBy(entry => entry.size)
                .OrderByDescending(group => group.Key)
                .ToList();

            var chosen = cohorts[0];
            var dropped = cohorts.Skip(1).SelectMany(group => group).ToList();
            if (dropped.Count > 0)
            {
                Console.WriteLine(
                    $"longmemeval: comparing the {chosen.Count()} run(s) of {chosen.Key} questions; "
                    + $"excluded {dropped.Count} run(s) of other sizes "
                    + $"({string.Join(", ", cohorts.Skip(1).Select(group => $"{group.Count()}x{group.Key}q"))}) "
                    + "-- different denominators are not repeats.");
            }

            used = chosen.Select(entry => used[entry.index]).ToList();
            runs = chosen.Select(entry => entry.rows).ToList();

            // Only measurable across repeats of the SAME arm. One run has no spread, and the calculator
            // is handed nothing rather than a single point, so the renderer prints "unmeasured".
            var noiseFloors = runs.Count > 1
                ? LongMemEvalTypedNoiseFloorCalculator.Measure(runs)
                : null;

            Console.WriteLine($"longmemeval: typed report for arm '{arm}' over {runs.Count} run(s):");
            foreach (var name in used) Console.WriteLine($"  {name}");
            Console.WriteLine();

            // The LAST run is the one rendered; earlier ones exist to give the band something to
            // measure. Rendering a mean would invent a run that never happened.
            var markdown = LongMemEvalTypedReport.Render(
                runs[^1], noiseFloors, ablations: null, LongMemEvalMemoryTypeMap.Default.Revision);

            Console.WriteLine(markdown);

            if (runs.Count == 1)
            {
                Console.WriteLine(
                    "NOTE: one run only, so the band is unmeasured rather than zero. Pass several "
                    + "reports of the same arm and configuration to measure it.");
            }

            var output = Value(args, "--output");
            if (output is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                File.WriteAllText(output, markdown);
                Console.WriteLine($"longmemeval: wrote {output}");
            }

            return Task.FromResult(0);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"longmemeval: typed report failed: {exception.Message}");
            return Task.FromResult(1);
        }
    }

    private static List<string> ResolveReports(string[] args)
    {
        var spec = Value(args, "--reports");
        var paths = new List<string>();

        if (spec is null)
        {
            var root = Path.Combine("artifacts", "evaluation");
            if (Directory.Exists(root))
                paths.AddRange(Directory.GetFiles(root, "*report*.json", SearchOption.AllDirectories));
        }
        else
        {
            foreach (var entry in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (Directory.Exists(entry))
                    paths.AddRange(Directory.GetFiles(entry, "*report*.json", SearchOption.AllDirectories));
                else if (File.Exists(entry))
                    paths.Add(entry);
            }
        }

        return paths.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Per-type rows for one arm of one report, or null when that report cannot be scored.
    /// </summary>
    private static IReadOnlyList<LongMemEvalTypedAccuracy>? ReadArm(string path, string arm)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(File.ReadAllText(path)); }
        catch (JsonException) { return null; }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("arms", out var arms) ||
                arms.ValueKind != JsonValueKind.Object ||
                !arms.TryGetProperty(arm, out var armElement))
            {
                return null;
            }

            if (!armElement.TryGetProperty("judgments", out var judgments) ||
                judgments.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var correct = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var judgment in judgments.EnumerateArray())
            {
                if (!judgment.TryGetProperty("QuestionId", out var id) ||
                    !judgment.TryGetProperty("Correct", out var verdict) ||
                    verdict.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                {
                    continue;
                }

                correct[id.GetString()!] = verdict.GetBoolean();
            }

            if (correct.Count == 0) return null;

            // Question types live on the telemetry rows, not on the judgments, so a report without
            // telemetry cannot be grouped by memory type at all. Skipped rather than guessed.
            if (!armElement.TryGetProperty("questions", out var questions) ||
                questions.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var telemetry = new List<LongMemEvalQuestionTelemetry>();
            foreach (var question in questions.EnumerateArray())
            {
                if (!question.TryGetProperty("QuestionId", out var id) ||
                    !question.TryGetProperty("QuestionType", out var type))
                {
                    continue;
                }

                // Only the identity and the type matter for grouping; the counters are irrelevant
                // here and are given their zero values rather than parsed and ignored.
                telemetry.Add(new LongMemEvalQuestionTelemetry(
                    QuestionNumber: telemetry.Count + 1,
                    MessagesStored: 0,
                    ItemsRetrieved: 0,
                    RecallTruncated: false)
                {
                    QuestionId = id.GetString(),
                    QuestionType = type.GetString(),
                });
            }

            return telemetry.Count == 0
                ? null
                : LongMemEvalTypedBreakdown.Summarise(telemetry, correct);
        }
    }

    private static string? Value(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}
