using System.Globalization;
using System.Text.Json;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Reads recorded runs off disk and reports, per memory type, how much of the failure a capture-side
/// change could possibly convert (8.3c).
/// </summary>
/// <remarks>
/// <para>
/// <b>Credential-free and free of charge</b>, like every other read-only verb here: deciding whether to
/// spend ~96M input tokens must not itself require the credentials of a paid run, or the question stops
/// being asked and the run gets bought instead.
/// </para>
/// <para>
/// Only runs whose <b>answer-presence gate actually evaluated</b> are counted. Most recorded runs predate
/// the gate, and pooling them silently would repeat 4.5's mistake — the pass that reported 3.4% accuracy
/// against a known 90% because it averaged in 51 runs that could not answer the question at all.
/// </para>
/// </remarks>
internal static class LongMemEvalCaptureHeadroomProgram
{
    /// <summary>
    /// LongMemEval labels questions by TASK; this maps those labels to memory types. Mirrors
    /// <c>Taxonomy/memory-type-map.json</c>, whose own header insists the mapping is an opinion.
    /// </summary>
    private static readonly Dictionary<string, string> MemoryTypeByTask = new(StringComparer.Ordinal)
    {
        ["single-session-user"] = "semantic",
        ["single-session-preference"] = "semantic",
        ["multi-session"] = "semantic",
        ["single-session-assistant"] = "episodic",
        ["temporal-reasoning"] = "temporal",
        ["knowledge-update"] = "temporal",
    };

    internal static int Run(string[] args)
    {
        var root = Directory(args);
        if (!System.IO.Directory.Exists(root))
        {
            Console.Error.WriteLine($"longmemeval: no such directory: {root}");
            return 1;
        }

        var reports = System.IO.Directory
            .EnumerateFiles(root, "prepared-pair-report.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        var byMode = new Dictionary<string, List<CaptureHeadroomQuestion>>(StringComparer.Ordinal);
        var gated = 0;

        foreach (var report in reports)
        {
            foreach (var (mode, questions) in ReadArms(report))
            {
                if (questions.Count == 0) continue;
                gated++;
                if (!byMode.TryGetValue(mode, out var bucket))
                    byMode[mode] = bucket = [];
                bucket.AddRange(questions);
            }
        }

        Console.WriteLine(
            $"capture-headroom: {reports.Count} report(s) scanned, {gated} arm(s) with a live presence gate");
        if (gated == 0)
        {
            // Not an error, and not silence either: an empty result here means "no recorded run can answer
            // this", which is a different statement from "there is no headroom" and must not read as one.
            Console.WriteLine(
                "  no recorded arm has an evaluated answer-presence gate; nothing can be concluded from disk");
            return 0;
        }

        foreach (var (mode, questions) in byMode.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  [{mode}]");
            foreach (var type in LongMemEvalCaptureHeadroom.Summarise(questions))
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"    {type.MemoryType,-10} n={type.Questions,3} acc={type.Accuracy,6:P1} "
                    + $"wrong={type.Failures,3} checkable={type.FailuresCheckable,3} "
                    + $"answerAlreadyPresent={type.FailuresAnswerPresent,3} "
                    + $"captureReachable={type.CaptureReachableFailures,3} "
                    + $"ceiling={type.CaptureCeiling,6:P1} "
                    + $"worthMeasuring={LongMemEvalCaptureHeadroom.WorthMeasuring(type)}"));
            }
        }

        Console.WriteLine(
            "  reading: 'captureReachable' counts failures whose gold answer was ABSENT from the assembled "
            + "context -- the only failures storing more could convert. A zero means a capture-side change "
            + "has nothing to convert on this evidence, however the arms score.");
        return 0;
    }

    /// <summary>
    /// Yields one bucket per arm, containing only questions whose presence gate was evaluated.
    /// </summary>
    private static IEnumerable<(string Mode, List<CaptureHeadroomQuestion> Questions)> ReadArms(string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(path));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // A malformed or half-written report is skipped rather than aborting the sweep: this verb
            // exists to summarise a directory of historical artifacts, and one bad file must not hide the
            // other nineteen.
            Console.Error.WriteLine($"longmemeval: skipping unreadable report {path}: {exception.Message}");
            yield break;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("arms", out var arms)
                || arms.ValueKind != JsonValueKind.Object)
            {
                yield break;
            }

            foreach (var arm in arms.EnumerateObject())
            {
                yield return (arm.Name, ReadArm(arm.Value));
            }
        }
    }

    private static List<CaptureHeadroomQuestion> ReadArm(JsonElement arm)
    {
        var presence = new Dictionary<string, (bool Checkable, bool Present)>(StringComparer.Ordinal);
        if (arm.ValueKind != JsonValueKind.Object) return [];
        if (arm.TryGetProperty("questions", out var telemetry) && telemetry.ValueKind == JsonValueKind.Array)
        {
            foreach (var question in telemetry.EnumerateArray())
            {
                if (!question.TryGetProperty("QuestionId", out var id) || id.ValueKind != JsonValueKind.String)
                    continue;
                if (!question.TryGetProperty("AnswerPresence", out var gate)
                    || gate.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                presence[id.GetString()!] = (
                    gate.TryGetProperty("Checkable", out var checkable)
                        && checkable.ValueKind == JsonValueKind.True,
                    gate.TryGetProperty("Present", out var present)
                        && present.ValueKind == JsonValueKind.True);
            }
        }

        var rows = new List<CaptureHeadroomQuestion>();
        // ValueKind is checked before descending, not just presence: a rejected arm serialises
        // "result": null, and TryGetProperty answers TRUE for a property whose value is JSON null.
        // Reading through it throws, which this verb hit on the first real directory it was pointed at.
        if (!arm.TryGetProperty("result", out var result)
            || result.ValueKind != JsonValueKind.Object
            || !result.TryGetProperty("QuestionResults", out var judged)
            || judged.ValueKind != JsonValueKind.Array)
        {
            return rows;
        }

        foreach (var question in judged.EnumerateArray())
        {
            if (!question.TryGetProperty("QuestionId", out var id) || id.ValueKind != JsonValueKind.String)
                continue;
            var questionId = id.GetString()!;
            if (!presence.TryGetValue(questionId, out var gate))
                continue;

            var task = question.TryGetProperty("QuestionType", out var type) && type.ValueKind == JsonValueKind.String
                ? type.GetString()!
                : "unknown";

            rows.Add(new CaptureHeadroomQuestion(
                QuestionId: questionId,
                // An unmapped task label keeps its own name rather than being folded into a type it was
                // never assigned to -- a wrong bucket is worse than an explicit unknown.
                MemoryType: MemoryTypeByTask.GetValueOrDefault(task, task),
                Correct: question.TryGetProperty("Correct", out var correct)
                    && correct.ValueKind == JsonValueKind.True,
                AnswerCheckable: gate.Checkable,
                AnswerPresent: gate.Present));
        }

        return rows;
    }

    private static string Directory(string[] args)
    {
        var index = Array.IndexOf(args, "--artifacts");
        return index >= 0 && index + 1 < args.Length
            ? args[index + 1]
            : Path.Combine("artifacts", "evaluation");
    }
}
