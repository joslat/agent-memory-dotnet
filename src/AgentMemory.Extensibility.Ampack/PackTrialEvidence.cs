using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// PACK-evidence-aef: a host's trial decision as an AEF run (the AgentEval Evidence Format, 1.0), so anyone can check it
/// with AEF's own tools and an assurer can seal and sign it. The subject is the pack (its id at its digest, its version);
/// two lanes hold the trial's tasks with the pack and without it, each task a result (passed when what the pack offered hit
/// the truth, its precision as a score); the gate's decision is a gate line (ship when the pack goes on), with every check,
/// both arms' measurements, the version it was compared with and the upgrade rule under <c>io.agentmemory.trial</c>; the
/// host-signed activation record itself travels in <c>ext/activation.dsse.json</c>. The run is written closed and
/// unsealed: the assurer seals it on ingestion (<c>agenteval aef seal --sealed-by ingest --key …</c>). Nothing of the
/// repository's content is kept (<c>contentCapture: off</c>).
/// </summary>
[Experimental("AMEXT001")]
public static class PackTrialEvidence
{
    /// <summary>The AEF version written.</summary>
    public const string SchemaVersion = "1.0";
    /// <summary>The lane of the tasks run with the pack.</summary>
    public const string WithLane = "with-pack";
    /// <summary>The lane of the tasks run without it (the host's baseline, or nothing).</summary>
    public const string WithoutLane = "without-pack";
    private const string Extension = "io.agentmemory.trial";
    private const string TaskPath = "task";

    /// <summary>
    /// Writes <paramref name="decision"/> as an AEF run in <paramref name="folder"/>, which must be absent or empty; with the
    /// host-signed record's bytes when given. Returns the run id.
    /// </summary>
    /// <exception cref="ArgumentException">The decision carries no measurement, or the folder is not empty.</exception>
    public static string WriteRun(PackActivation decision, string folder, byte[]? signedRecord = null, string producerVersion = "2.0.0")
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        if (decision.With is not { } with) throw new ArgumentException("The decision carries no measurement to write as evidence.", nameof(decision));
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new ArgumentException($"{folder} is not empty.", nameof(folder));
        Directory.CreateDirectory(folder);

        var at = Time(decision.DecidedAt);
        var runId = Id($"trial.{decision.ModuleId}.{decision.Version}.{decision.DecidedAt.UtcDateTime:yyyyMMddTHHmmssfffZ}");
        var subject = decision.PackDigest is { } digest ? $"pack:{decision.ModuleId}@{digest}" : $"module:{decision.ModuleId}@{decision.ManifestDigest}";
        var run = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion, ["runId"] = runId, ["status"] = "completed",
            ["producer"] = new JsonObject { ["name"] = "agentmemory-pack-gate", ["version"] = producerVersion },
            ["subject"] = new JsonObject { ["ref"] = Ref(subject), ["kind"] = "other", ["version"] = decision.Version },
            ["suite"] = new JsonObject { ["ref"] = Ref("trial:" + decision.ModuleId), ["version"] = decision.ManifestDigest },
            ["execution"] = new JsonObject { ["targetMode"] = "live", ["stimulus"] = "other" },
            ["startedAt"] = at, ["endedAt"] = at, ["contentCapture"] = "off",
            ["config"] = new JsonObject { ["thresholds"] = Thresholds(decision) },
        };

        var lines = new List<JsonObject>();
        var lanes = new List<(string Lane, List<JsonObject> Lines)>();
        foreach (var (lane, arm) in new[] { (WithLane, with), (WithoutLane, decision.Without) })
        {
            if (arm is null) continue;
            var armLines = Arm(runId, lane, arm);
            lines.AddRange(armLines);
            lanes.Add((lane, armLines));
        }
        var usesArmScores = lines.Any(l => l["path"]!.GetValue<string>() == "arm");
        var metrics = new JsonArray(
            Metric("hit", "rate", "Whether what the pack offered for a task hit the truth (a changed test file)."),
            Metric("precision", "score", "The share of what was offered for a task that was in the truth."));
        if (usesArmScores)
        {
            metrics.Add(Metric("hit-rate", "score", "The share of the arm's tasks it hit, when its tasks were not kept one by one."));
            metrics.Add(Metric("pooled-precision", "score", "The share of everything the arm offered that was in the truth."));
        }

        var summary = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion, ["runId"] = runId,
            ["lanes"] = new JsonArray([.. lanes.Select(l => (JsonNode)new JsonObject
            {
                ["lane"] = l.Lane,
                ["metrics"] = l.Lines.Any(x => x["path"]!.GetValue<string>() == TaskPath)
                    ? new JsonArray(Entry(l.Lines, "hit", "rate"), Entry(l.Lines, "precision", "score"))
                    : new JsonArray(Entry(l.Lines, "hit-rate", "score", "arm"), Entry(l.Lines, "pooled-precision", "score", "arm")),
            })]),
        };

        var withLines = lanes.FirstOrDefault(l => l.Lane == WithLane).Lines ?? [];
        var gate = new JsonObject
        {
            ["schemaVersion"] = SchemaVersion, ["gateId"] = Ref("gate:agentmemory/pack-trial"), ["decisionId"] = Id(runId + ".decision"),
            ["rule"] = new JsonObject
            {
                ["strategy"] = "lower confidence bounds against the pack's declared targets; an upgrade no worse than the version it replaces",
                ["inputs"] = new JsonArray([.. decision.Checks.Select(c => (JsonNode)c.Target)]),
            },
            ["inputs"] = new JsonObject { ["results"] = new JsonArray([.. withLines.Select(l => (JsonNode)l["resultId"]!.GetValue<string>())]) },
            ["comparability"] = decision.Previous is null ? "not_applicable" : "comparable",
            ["outcome"] = decision.Active ? "ship" : "no_ship", ["exitCode"] = decision.Active ? 0 : 1, ["decidedAt"] = at,
            ["ext"] = new JsonObject { [Extension] = Facts(decision) },
        };

        Write(Path.Combine(folder, "run.json"), run);
        File.WriteAllText(Path.Combine(folder, "results.ndjson"), string.Concat(lines.Select(l => l.ToJsonString() + "\n")), Utf8);
        Write(Path.Combine(folder, "metrics.json"), new JsonObject { ["schemaVersion"] = SchemaVersion, ["metrics"] = metrics });
        Write(Path.Combine(folder, "summary.json"), summary);
        File.WriteAllText(Path.Combine(folder, "gates.ndjson"), gate.ToJsonString() + "\n", Utf8);
        if (signedRecord is not null)
        {
            Directory.CreateDirectory(Path.Combine(folder, "ext"));
            File.WriteAllBytes(Path.Combine(folder, "ext", "activation.dsse.json"), signedRecord);
        }
        return runId;
    }

    // One arm's results: each task a line (passed when it hit), or, for an arm kept only in total, one scored line.
    private static List<JsonObject> Arm(string runId, string lane, PackTrialMeasurement arm)
    {
        var evaluator = new JsonObject { ["id"] = "agentmemory.pack-trial.test-impact", ["version"] = "1" };
        if (arm.PerTask is not { Count: > 0 } tasks)
        {
            var caseId = lane + "/all";
            return [new JsonObject
            {
                ["schemaVersion"] = SchemaVersion, ["resultId"] = ResultId(runId, caseId, "arm"), ["caseId"] = caseId, ["path"] = "arm", ["lane"] = lane,
                ["evaluator"] = evaluator.DeepClone(), ["state"] = "scored",
                ["scores"] = new JsonArray(Score("hit-rate", arm.HitRate), Score("pooled-precision", arm.Precision)),
                ["ext"] = new JsonObject { [Extension] = Measurement(arm) },
            }];
        }
        var lines = new List<JsonObject>(tasks.Count);
        for (var i = 0; i < tasks.Count; i++)
        {
            var caseId = $"{lane}/task-{i + 1:D3}";
            var line = new JsonObject
            {
                ["schemaVersion"] = SchemaVersion, ["resultId"] = ResultId(runId, caseId, TaskPath), ["caseId"] = caseId, ["path"] = TaskPath, ["lane"] = lane,
                ["evaluator"] = evaluator.DeepClone(), ["state"] = tasks[i].Correct > 0 ? "passed" : "failed",
                ["ext"] = new JsonObject { [Extension] = new JsonObject { ["offered"] = tasks[i].Offered, ["correct"] = tasks[i].Correct } },
            };
            // Precision is a share of what was offered: a task that offered nothing has none (it is still a miss).
            if (tasks[i].Offered > 0) line["scores"] = new JsonArray(Score("precision", (double)tasks[i].Correct / tasks[i].Offered));
            lines.Add(line);
        }
        return lines;
    }

    // A summary entry, as [SUM-3]–[SUM-6] define it: a rate counts passed lines as 1; a score, the lines that carry it.
    private static JsonObject Entry(List<JsonObject> lines, string metric, string kind, string path = TaskPath)
    {
        var values = new List<double>();
        var counted = 0;
        foreach (var line in lines.Where(l => l["path"]!.GetValue<string>() == path))
        {
            counted++;
            var state = line["state"]!.GetValue<string>();
            if (kind == "rate") { if (state != "scored") values.Add(state == "passed" ? 1 : 0); continue; }
            if (line["scores"] is JsonArray scores && scores.FirstOrDefault(s => s!["metric"]!.GetValue<string>() == metric) is { } score)
                values.Add(score["value"]!.GetValue<double>());
        }
        var sum = ExactSum(values);
        var entry = new JsonObject
        {
            ["metric"] = metric, ["path"] = path, ["N"] = counted, ["n"] = values.Count, ["notMeasured"] = counted - values.Count,
            ["value"] = values.Count == 0 ? null : sum / values.Count, ["verdict"] = values.Count == 0 ? "not_measured" : "scored",
        };
        if (values.Count > 0) entry["sum"] = sum;
        return entry;
    }

    // The decision's facts the format has no field for, under the producer's extension.
    private static JsonObject Facts(PackActivation decision)
    {
        var facts = new JsonObject
        {
            ["moduleId"] = decision.ModuleId, ["version"] = decision.Version, ["manifestDigest"] = decision.ManifestDigest,
            ["packDigest"] = decision.PackDigest, ["active"] = decision.Active, ["reason"] = decision.Reason,
            ["checks"] = new JsonArray([.. decision.Checks.Select(c => (JsonNode)new JsonObject
            {
                ["target"] = c.Target, ["required"] = c.Required, ["observed"] = c.Observed, ["met"] = c.Met, ["estimate"] = c.Estimate,
            })]),
        };
        if (decision.With is { } with) facts["with"] = Measurement(with);
        if (decision.Without is { } without) facts["without"] = Measurement(without);
        if (decision.Previous is { } previous)
            facts["previous"] = new JsonObject
            {
                ["version"] = previous.Version, ["manifestDigest"] = previous.ManifestDigest, ["packDigest"] = previous.PackDigest, ["with"] = Measurement(previous.With),
            };
        if (decision.Upgrade is { } upgrade) facts["upgrade"] = new JsonObject { ["noRegression"] = upgrade.NoRegression, ["tolerance"] = upgrade.Tolerance };
        return facts;
    }

    private static JsonObject Measurement(PackTrialMeasurement arm) => new()
    {
        ["tasks"] = arm.Tasks, ["hits"] = arm.Hits, ["offered"] = arm.Offered, ["correct"] = arm.Correct, ["p95Ms"] = arm.P95Ms,
    };

    private static JsonObject Thresholds(PackActivation decision)
    {
        var thresholds = new JsonObject();
        foreach (var check in decision.Checks)
            thresholds[check.Target] = new JsonObject { ["op"] = check.Target == "addedP95Ms" ? "<=" : ">=", ["value"] = check.Required };
        return thresholds;
    }

    private static JsonObject Metric(string id, string kind, string description) => new()
    {
        ["id"] = id, ["kind"] = kind, ["direction"] = "higher_better", ["scale"] = new JsonObject { ["min"] = 0, ["max"] = 1 }, ["description"] = description,
    };

    private static JsonObject Score(string metric, double value) => new() { ["metric"] = metric, ["value"] = value };

    /// <summary>[RES-4]: <c>r_</c> and the first 32 hex characters of the SHA-256 of runId, caseId, path and trial joined by U+001F.</summary>
    public static string ResultId(string runId, string caseId, string path, int? trial = null) =>
        "r_" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\u001F', runId, caseId, path, trial?.ToString(CultureInfo.InvariantCulture) ?? ""))))[..32];

    /// <summary>
    /// The exact sum of binary64 values rounded once, as [SUM-5] requires (and Python's <c>math.fsum</c> computes): Shewchuk's
    /// partials, so cancellation never loses a value.
    /// </summary>
    public static double ExactSum(IEnumerable<double> values)
    {
        var partials = new List<double>();
        foreach (var value in values)
        {
            var x = value;
            var kept = 0;
            for (var i = 0; i < partials.Count; i++)
            {
                var y = partials[i];
                if (Math.Abs(x) < Math.Abs(y)) (x, y) = (y, x);
                var high = x + y;
                var low = y - (high - x);
                if (low != 0) partials[kept++] = low;
                x = high;
            }
            partials.RemoveRange(kept, partials.Count - kept);
            partials.Add(x);
        }
        // Round the partials to one value, as fsum does: from the largest down, with the half-way correction.
        var n = partials.Count;
        if (n == 0) return 0;
        var total = partials[--n];
        var lowPart = 0.0;
        while (n > 0)
        {
            var x = total;
            var y = partials[--n];
            total = x + y;
            var yr = total - x;
            lowPart = y - yr;
            if (lowPart != 0) break;
        }
        if (n > 0 && (lowPart < 0 && partials[n - 1] < 0 || lowPart > 0 && partials[n - 1] > 0))
        {
            var y = lowPart * 2;
            var x = total + y;
            if (y == x - total) total = x;
        }
        return total;
    }

    // AEF ids: letters, digits and . _ : - only, at most 128 characters.
    private static string Id(string text)
    {
        var id = new string([.. text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or ':' or '-' ? c : '-')]);
        return id.Length <= 128 ? id : id[..95] + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    // A typed reference: kind:name, the name printable ASCII, at most 256 characters.
    private static string Ref(string text) => text.Length <= 289 ? text : text[..289];

    private static string Time(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    private static void Write(string path, JsonObject value) => File.WriteAllText(path, value.ToJsonString(), Utf8);
}
