using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Cli.Perf;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// <c>perf run</c> — measures what an agent turn actually costs and writes a durable, dated record.
/// </summary>
/// <remarks>
/// <para>
/// This is the turn-level counterpart to <c>evaluate</c>. <c>evaluate</c> measures repository
/// operations; nothing before this measured a complete recall → model → persist cycle, and nothing
/// counted database round trips, embedding requests, or model calls at all. Those counts are the
/// numbers every optimization on the performance roadmap has to move.
/// </para>
/// <para>
/// Counters are exact and machine-independent; timings are recorded but are only meaningful as ratios
/// within a run. The report keeps them visibly separate for that reason.
/// </para>
/// </remarks>
public sealed class PerfCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TextWriter _output;

    public PerfCommand(TextWriter output) => _output = output;

    public async Task<int> ExecuteAsync(
        string? label,
        string? scenarioFilter,
        string? iterationsValue,
        string? warmupValue,
        string? dimensionsValue,
        string? latency,
        string? outputRoot,
        CancellationToken cancellationToken = default)
    {
        var runLabel = Sanitize(label) ?? "baseline";
        var iterations = ParsePositive(iterationsValue, 10, "iterations");
        var warmup = ParseNonNegative(warmupValue, 3, "warmup");
        var dimensions = ParsePositive(dimensionsValue, 384, "embedding-dimensions");

        var (embeddingLatency, modelLatency) = ResolveLatency(latency);

        List<PerfScenario> scenarios;
        try
        {
            scenarios = PerfScenarios.Select(scenarioFilter).ToList();
        }
        catch (ArgumentException ex)
        {
            _output.WriteLine($"error: {ex.Message}");
            return 1;
        }

        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"{startedAt:yyyyMMdd'T'HHmmss'Z'}__{runLabel}__hermetic-S-{LatencyName(latency)}";
        var runDir = Path.Combine(outputRoot ?? Path.Combine("performance", "runs"), runId);
        Directory.CreateDirectory(runDir);

        _output.WriteLine($"perf: run {runId}");

        using var trace = new TraceLogWriter(Path.Combine(runDir, "trace.ndjson"));
        var manifest = BuildManifest(runId, runLabel, startedAt, iterations, warmup, dimensions,
            embeddingLatency, modelLatency, scenarios);
        trace.RunStart(runId, manifest);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "run.json"), JsonSerializer.Serialize(manifest, Json), cancellationToken)
            .ConfigureAwait(false);

        var runStopwatch = Stopwatch.StartNew();
        using var collector = new PerfCollector(trace);

        await using var profile = await HermeticProfile
            .StartAsync(dimensions, embeddingLatency, modelLatency, _output, cancellationToken)
            .ConfigureAwait(false);

        await PerfFixture.SeedAsync(profile, _output, cancellationToken).ConfigureAwait(false);

        var provider = PerfScenarios.CreateProvider(profile);

        foreach (var scenario in scenarios)
        {
            _output.WriteLine($"perf: {scenario.Id} — {scenario.Description}");
            try
            {
                await RunScenarioAsync(scenario, profile, provider, collector, warmup, iterations, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A failed self-check means the measurement would be misleading, not merely incomplete.
                // Fail the run rather than write a plausible-looking but wrong baseline.
                _output.WriteLine($"error: {scenario.Id} failed: {ex.Message}");
                trace.RunEnd(collector.Records.Count, runStopwatch.Elapsed.TotalMilliseconds);
                return 1;
            }
        }

        runStopwatch.Stop();
        trace.RunEnd(collector.Records.Count, runStopwatch.Elapsed.TotalMilliseconds);

        var measured = collector.Records.Where(r => r.Phase == "measure").ToList();
        await WriteSamplesAsync(runDir, measured, cancellationToken).ConfigureAwait(false);
        var summary = BuildSummary(manifest, measured);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "summary.json"), JsonSerializer.Serialize(summary, Json), cancellationToken)
            .ConfigureAwait(false);

        var report = RenderReport(runId, measured);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), report, cancellationToken)
            .ConfigureAwait(false);

        _output.WriteLine();
        _output.Write(report);
        _output.WriteLine($"perf: wrote {runDir}");
        return 0;
    }

    private static async Task RunScenarioAsync(
        PerfScenario scenario,
        HermeticProfile profile,
        AgentMemory.AgentFramework.Neo4jMemoryContextProvider provider,
        PerfCollector collector,
        int warmup,
        int iterations,
        CancellationToken cancellationToken)
    {
        // Warm-up samples are recorded (so they can be inspected in the trace) but carry Phase="warmup"
        // and are excluded from every aggregate by construction rather than by convention. JIT, the
        // connection pool, and Neo4j's query-plan cache all need warming; mixing those samples into a
        // percentile is one of the standard ways a benchmark misleads.
        for (var i = 0; i < warmup; i++)
        {
            using var turn = collector.BeginTurn(scenario.Id, i, "warmup");
            await scenario.RunAsync(new ScenarioContext(
                profile, provider, turn.Record, i, "warmup", cancellationToken)).ConfigureAwait(false);
        }

        for (var i = 0; i < iterations; i++)
        {
            using var turn = collector.BeginTurn(scenario.Id, i, "measure");
            await scenario.RunAsync(new ScenarioContext(
                profile, provider, turn.Record, i, "measure", cancellationToken)).ConfigureAwait(false);
        }
    }

    private static object BuildManifest(
        string runId, string label, DateTimeOffset startedAt, int iterations, int warmup, int dimensions,
        TimeSpan embeddingLatency, TimeSpan modelLatency, IReadOnlyList<PerfScenario> scenarios) => new
        {
            runId,
            label,
            startedAtUtc = startedAt,
            profile = "hermetic",
            scale = "S",
            scenarios = scenarios.Select(s => s.Id).ToArray(),
            iterations,
            warmup,
            // The fingerprint is what makes two reports comparable — or, more importantly, what makes it
            // visible when they are not. A run without one is not comparable to anything.
            environment = new
            {
                commit = TryGetCommit(),
                embeddingDimensions = dimensions,
                embeddingLatencyMs = embeddingLatency.TotalMilliseconds,
                modelLatencyMs = modelLatency.TotalMilliseconds,
                neo4jImage = "neo4j:5.26",
                os = Environment.OSVersion.ToString(),
                processorCount = Environment.ProcessorCount,
                runtime = Environment.Version.ToString(),
                serverGc = System.Runtime.GCSettings.IsServerGC,
                machineName = Environment.MachineName,
            },
        };

    /// <summary>
    /// Best-effort commit SHA, with a dirty marker. Null rather than a guess when git is unavailable —
    /// a wrong commit in a fingerprint is worse than an absent one, because it silently licenses a
    /// comparison between two different trees.
    /// </summary>
    private static string? TryGetCommit()
    {
        try
        {
            var sha = RunGit("rev-parse HEAD");
            if (sha is null) return null;
            var dirty = !string.IsNullOrWhiteSpace(RunGit("status --porcelain"));
            return dirty ? $"{sha}-dirty" : sha;
        }
        catch
        {
            return null;
        }
    }

    private static string? RunGit(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("git", arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        });
        if (process is null) return null;
        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return process.ExitCode == 0 ? stdout.Trim() : null;
    }

    private static object BuildSummary(object manifest, IReadOnlyList<TurnRecord> measured) => new
    {
        manifest,
        scenarios = measured
            .GroupBy(r => r.Scenario, StringComparer.Ordinal)
            .Select(g => new
            {
                scenario = g.Key,
                samples = g.Count(),
                // Counters are reported as exact values, with a min/max so a non-deterministic counter
                // is visible rather than silently averaged into a plausible-looking number.
                counters = AllKeys(g).ToDictionary(
                    key => key,
                    key => new
                    {
                        median = Median(g.Select(r => (double)r.Counter(key)).ToList()),
                        min = g.Min(r => r.Counter(key)),
                        max = g.Max(r => r.Counter(key)),
                        deterministic = g.Min(r => r.Counter(key)) == g.Max(r => r.Counter(key)),
                    },
                    StringComparer.Ordinal),
                durationMs = Percentiles(g.Select(r => r.DurationMs).ToList()),
                spansMs = AllSpans(g).ToDictionary(
                    name => name,
                    name => Percentiles(g.Select(r =>
                        r.SpanMilliseconds.TryGetValue(name, out var ms) ? ms : 0d).ToList()),
                    StringComparer.Ordinal),
            })
            .ToList(),
    };

    private static IEnumerable<string> AllKeys(IEnumerable<TurnRecord> records) =>
        records.SelectMany(r => r.Counters.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);

    private static IEnumerable<string> AllSpans(IEnumerable<TurnRecord> records) =>
        records.SelectMany(r => r.SpanMilliseconds.Keys).Distinct(StringComparer.Ordinal).OrderBy(k => k, StringComparer.Ordinal);

    private static async Task WriteSamplesAsync(
        string runDir, IReadOnlyList<TurnRecord> measured, CancellationToken cancellationToken)
    {
        var lines = measured.Select(r => JsonSerializer.Serialize(new
        {
            scenario = r.Scenario,
            iteration = r.Iteration,
            durMs = r.DurationMs,
            counters = r.Counters,
            spansMs = r.SpanMilliseconds,
        }));
        await File.WriteAllLinesAsync(Path.Combine(runDir, "samples.ndjson"), lines, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string RenderReport(string runId, IReadOnlyList<TurnRecord> measured)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Performance run — {runId}");
        sb.AppendLine();

        foreach (var group in measured.GroupBy(r => r.Scenario, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}  ({group.Count()} measured iterations)");
            sb.AppendLine();
            sb.AppendLine("### Structural counters (exact — these are what CI may gate on)");
            sb.AppendLine();
            sb.AppendLine("| Counter | Value | Deterministic |");
            sb.AppendLine("|---|---:|---|");
            foreach (var key in AllKeys(group))
            {
                var min = group.Min(r => r.Counter(key));
                var max = group.Max(r => r.Counter(key));
                var stable = min == max;
                var value = stable ? min.ToString(CultureInfo.InvariantCulture) : $"{min}–{max}";
                sb.AppendLine(CultureInfo.InvariantCulture, $"| `{key}` | {value} | {(stable ? "yes" : "**NO**")} |");
            }

            sb.AppendLine();
            sb.AppendLine("### Timings (comparable only within this run)");
            sb.AppendLine();
            sb.AppendLine(
                "Per-span figures are the **sum across all occurrences in one iteration**, not elapsed " +
                "time. Recall fans out concurrently, so a summed span total legitimately exceeds the " +
                "iteration's wall clock — `memory.db.tx` summing to several hundred ms inside a 50 ms " +
                "iteration means many overlapping transactions, not a slow database. Only **iteration " +
                "total** is elapsed time.");
            sb.AppendLine();
            sb.AppendLine("| Span | n per iteration | summed p50 ms | summed p95 ms | mean per occurrence ms |");
            sb.AppendLine("|---|---:|---:|---:|---:|");

            var total = Percentiles(group.Select(r => r.DurationMs).ToList());
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| **iteration total** (elapsed) | 1 | {total.p50:F2} | {total.p95:F2} | {total.p50:F2} |");

            foreach (var name in AllSpans(group))
            {
                var summed = Percentiles(group.Select(r =>
                    r.SpanMilliseconds.TryGetValue(name, out var ms) ? ms : 0d).ToList());
                var occurrences = Median(group.Select(r =>
                    (double)(r.SpanCounts.TryGetValue(name, out var n) ? n : 0)).ToList());
                var perOccurrence = occurrences > 0 ? summed.p50 / occurrences : 0d;
                var note = name == "memory.db.query" ? " ⚠" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{name}`{note} | {occurrences:F0} | {summed.p50:F2} | {summed.p95:F2} | {perOccurrence:F2} |");
            }

            sb.AppendLine();
            sb.AppendLine(
                "⚠ `memory.db.query` measures **query dispatch only** — the driver returns a cursor and " +
                "the records are streamed afterwards, inside the enclosing transaction but outside this " +
                "span. Its *count* is exact and is the number to use; its *duration* substantially " +
                "understates real query cost. Record-level timing needs cursor wrapping (a later step).");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static (double p50, double p95, double min, double max) Percentiles(List<double> values)
    {
        if (values.Count == 0) return (0, 0, 0, 0);
        values.Sort();
        return (Quantile(values, 0.50), Quantile(values, 0.95), values[0], values[^1]);
    }

    private static double Quantile(List<double> sorted, double q)
    {
        if (sorted.Count == 1) return sorted[0];
        var position = (sorted.Count - 1) * q;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper) return sorted[lower];
        return sorted[lower] + (sorted[upper] - sorted[lower]) * (position - lower);
    }

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0;
        values.Sort();
        return Quantile(values, 0.50);
    }

    private static (TimeSpan Embedding, TimeSpan Model) ResolveLatency(string? latency) =>
        latency?.ToLowerInvariant() switch
        {
            // Isolates database and CPU cost — no injected waiting anywhere.
            null or "zero" => (TimeSpan.Zero, TimeSpan.Zero),
            // Reproduces the shape of a same-region remote deployment, so ordering and overlap
            // optimizations are measurable without a network dependency.
            "remote" => (TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(900)),
            _ => throw new ArgumentException($"unknown --latency '{latency}'. Use 'zero' or 'remote'."),
        };

    private static string LatencyName(string? latency) =>
        string.IsNullOrWhiteSpace(latency) ? "zero" : latency.ToLowerInvariant();

    private static int ParsePositive(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed <= 0)
            throw new ArgumentException($"--{name} must be a positive integer.");
        return parsed;
    }

    private static int ParseNonNegative(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) || parsed < 0)
            throw new ArgumentException($"--{name} must be zero or a positive integer.");
        return parsed;
    }

    /// <summary>Keeps the label safe for a directory name without silently mangling it.</summary>
    private static string? Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var invalid = Path.GetInvalidFileNameChars().Concat(['_', ' ']).ToArray();
        return new string(label.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }
}
