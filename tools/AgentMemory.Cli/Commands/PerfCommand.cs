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
        string? qualityGateValue,
        CancellationToken cancellationToken = default)
    {
        var runLabel = Sanitize(label) ?? "baseline";
        var iterations = ParsePositive(iterationsValue, 10, "iterations");
        var warmup = ParseNonNegative(warmupValue, 3, "warmup");
        var dimensions = ParsePositive(dimensionsValue, 384, "embedding-dimensions");
        var qualityGateEnabled = ParseDefaultTrue(qualityGateValue, "quality-gate");
        var qualityBaseline = qualityGateEnabled ? QualityGate.LoadBaseline() : null;

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
        // artifacts/ is the repository's existing home for generated reports (the `evaluate` verb writes
        // to artifacts/evaluation), and it is already gitignored. Run output is regenerable and must not
        // live beside the hand-written analysis documents.
        var runDir = Path.Combine(outputRoot ?? Path.Combine("artifacts", "perf"), runId);
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

        // The extraction fixture and cost scenarios supply input-keyed model answers, so they must be
        // assembled before the profile that wires the chat client.
        var extractionFixture = ExtractionQualityFixture.Load();
        var scriptedRules = extractionFixture.ScriptedRules().Concat(PerfScenarios.ScriptedRules).ToList();

        await using var profile = await HermeticProfile
            .StartAsync(dimensions, embeddingLatency, modelLatency, _output,
                scriptedRules, cancellationToken)
            .ConfigureAwait(false);

        await PerfFixture.SeedAsync(profile, _output, cancellationToken).ConfigureAwait(false);

        // The quality guard shares the run so its scores sit beside the counters that were measured on
        // the same code, same data, same moment. Reporting speed and quality from separate runs is how
        // a regression gets attributed to the wrong change.
        var quality = new QualityEvaluator(QualityFixture.Load(), profile.Services, dimensions);
        await quality.SeedAsync(_output, cancellationToken).ConfigureAwait(false);

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

        _output.WriteLine("perf: scoring retrieval quality…");
        var qualityResult = await quality.EvaluateAsync(cancellationToken).ConfigureAwait(false);

        _output.WriteLine("perf: scoring extraction quality…");
        var extractionQuality = await new ExtractionQualityEvaluator(extractionFixture, profile.Services, profile.Driver)
            .EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var qualityGate = qualityBaseline is null
            ? QualityGateResult.Disabled()
            : QualityGate.Evaluate(qualityBaseline, qualityResult, extractionQuality);

        runStopwatch.Stop();
        trace.RunEnd(collector.Records.Count, runStopwatch.Elapsed.TotalMilliseconds);

        var measured = collector.Records.Where(r => r.Phase == "measure").ToList();
        await WriteSamplesAsync(runDir, measured, cancellationToken).ConfigureAwait(false);
        var summary = BuildSummary(manifest, measured, qualityGate, qualityResult, extractionQuality);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "summary.json"), JsonSerializer.Serialize(summary, Json), cancellationToken)
            .ConfigureAwait(false);

        var report = RenderReport(runId, measured, qualityGate, qualityResult, extractionQuality);
        await File.WriteAllTextAsync(Path.Combine(runDir, "report.md"), report, cancellationToken)
            .ConfigureAwait(false);

        _output.WriteLine();
        _output.Write(report);
        _output.WriteLine($"perf: wrote {runDir}");
        if (qualityGate.Passed)
            return 0;

        _output.WriteLine($"error: quality gate failed with {qualityGate.Violations.Count} violation(s).");
        foreach (var violation in qualityGate.Violations)
            _output.WriteLine($"error:   {violation}");
        return 1;
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
            await RunIterationAsync(
                scenario, profile, provider, collector, i, "warmup", cancellationToken).ConfigureAwait(false);
        }

        for (var i = 0; i < iterations; i++)
        {
            await RunIterationAsync(
                scenario, profile, provider, collector, i, "measure", cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task RunIterationAsync(
        PerfScenario scenario,
        HermeticProfile profile,
        AgentMemory.AgentFramework.Neo4jMemoryContextProvider provider,
        PerfCollector collector,
        int iteration,
        string phase,
        CancellationToken cancellationToken)
    {
        await scenario.PrepareAsync(new ScenarioSetupContext(
            profile, iteration, phase, null, cancellationToken)).ConfigureAwait(false);

        TurnRecord record;
        using (var turn = collector.BeginTurn(scenario.Id, iteration, phase))
        {
            record = turn.Record;
            await scenario.ExecuteAsync(new ScenarioContext(
                profile, provider, record, iteration, phase, null,
                AgentMemory.Abstractions.Options.RecallOptions.Default, cancellationToken)).ConfigureAwait(false);
        }

        // Verification is deliberately outside the measured turn. Scenarios may read their writes back
        // to prove learning occurred; charging that harness-only query to the product path would corrupt
        // both elapsed time and database counters.
        await scenario.ValidateAsync(new ScenarioVerificationContext(
            profile, record, iteration, phase, null, cancellationToken)).ConfigureAwait(false);
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
            scenarioDependencyLatency = scenarios
                .Where(s => s.DependencyLatency is not null)
                .ToDictionary(
                    s => s.Id,
                    s => new
                    {
                        name = s.DependencyLatency!.Name,
                        embeddingDelayMs = s.DependencyLatency.EmbeddingDelay.TotalMilliseconds,
                        databaseDelayMs = s.DependencyLatency.DatabaseDelay.TotalMilliseconds,
                    }),
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

    private static object BuildSummary(
        object manifest, IReadOnlyList<TurnRecord> measured, QualityGateResult qualityGate,
        QualityResult quality,
        ExtractionQualityResult extraction) => new
    {
        manifest,
        qualityGate = new
        {
            enabled = qualityGate.Enabled,
            passed = qualityGate.Passed,
            baseline = qualityGate.BaselinePath,
            tolerance = qualityGate.Tolerance,
            violations = qualityGate.Violations,
        },
        extractionQuality = new
        {
            entityPrecision = extraction.EntityPrecision,
            entityRecall = extraction.EntityRecall,
            factPrecision = extraction.FactPrecision,
            factRecall = extraction.FactRecall,
            preferencePrecision = extraction.PreferencePrecision,
            preferenceRecall = extraction.PreferenceRecall,
            cases = extraction.Cases,
            expectNothingCases = extraction.ExpectNothingCases,
            falsePositives = extraction.FalsePositives,
            falsePositiveRate = extraction.FalsePositiveRate,
            clean = extraction.Clean,
            caseResults = extraction.CaseResults,
        },
        quality = new
        {
            recallAtK = quality.RecallAtK,
            mrr = quality.Mrr,
            cases = quality.Cases,
            casesWithViolations = quality.CasesWithViolations,
            clean = quality.Clean,
            recallByCategory = quality.RecallByCategory,
            caseResults = quality.CaseResults,
        },
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

    /// <summary>
    /// The unit a counter is expressed in. Stated explicitly on every row because the single most
    /// common way a performance number gets misread is a reader assuming milliseconds.
    /// </summary>
    private static string UnitFor(string counter) => counter switch
    {
        "neo4j.tx.read" => "read transactions",
        "neo4j.tx.write" => "write transactions",
        "neo4j.queries" => "Cypher queries",
        "embed.requests" => "provider requests",
        "embed.items" => "texts embedded",
        "embed.chars" => "characters",
        "llm.calls" => "model completions",
        "llm.tokens_in" => "input tokens",
        "llm.tokens_out" => "output tokens",
        "access_tracking.items" => "per-item writes",
        "store.messages" => "messages persisted",
        "context.messages" => "prompt messages",
        "context.chars" or "recall.chars" => "characters",
        "items.retrieved" => "memory items",
        _ when counter.StartsWith("items.", StringComparison.Ordinal) => "memory items",
        "graphrag.calls" => "GraphRAG requests",
        _ when counter.EndsWith("_delay.calls", StringComparison.Ordinal) => "injected waits",
        _ when counter.EndsWith("_delay.ms", StringComparison.Ordinal) => "configured milliseconds",
        _ => "count",
    };

    /// <summary>
    /// Total and per-occurrence time for the span backing a counter, when one exists. Returns nulls for
    /// counters with no corresponding span rather than inventing a number.
    /// </summary>
    private static (double? Total, double? Mean) TimingFor(string counter, IEnumerable<TurnRecord> group)
    {
        var span = counter switch
        {
            "neo4j.tx.read" or "neo4j.tx.write" => "memory.db.tx",
            "access_tracking.items" => "memory.recall.access_tracking",
            "store.messages" => "memory.store.messages",
            _ => null,
        };
        if (span is null) return (null, null);

        var records = group.ToList();
        var total = Median(records.Select(r =>
            r.SpanMilliseconds.TryGetValue(span, out var ms) ? ms : 0d).ToList());

        // Mean is over the counter's own occurrences, not the span's, so "mean ms per write transaction"
        // divides transaction time by transactions. For db.tx the span covers reads AND writes, so the
        // mean is across all transactions in that span and is labelled as an approximation by the note
        // under the table rather than silently attributed to one access mode.
        var occurrences = Median(records.Select(r =>
            (double)(r.SpanCounts.TryGetValue(span, out var n) ? n : 0)).ToList());

        return (total, occurrences > 0 ? total / occurrences : null);
    }

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

    private static string RenderReport(
        string runId, IReadOnlyList<TurnRecord> measured, QualityGateResult qualityGate,
        QualityResult quality,
        ExtractionQualityResult extraction)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Performance run — {runId}");
        sb.AppendLine();

        // Quality first, deliberately. A speed number read without the quality number beside it is how
        // "we got 96% faster" ships without the second sentence.
        sb.AppendLine("## Quality gate");
        sb.AppendLine();
        if (!qualityGate.Enabled)
        {
            sb.AppendLine("**DISABLED** by `--quality-gate=false`; scores below are report-only.");
        }
        else
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"**{(qualityGate.Passed ? "PASS ✅" : "FAIL ❌")}** · baseline " +
                $"`{qualityGate.BaselinePath}` · tolerance {qualityGate.Tolerance:F6}");
            if (!qualityGate.Passed)
            {
                sb.AppendLine();
                foreach (var violation in qualityGate.Violations)
                    sb.AppendLine($"- {violation}");
            }
        }
        sb.AppendLine();

        sb.AppendLine("## Retrieval quality (deterministic — no model involved)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"**Recall@K {quality.RecallAtK:F3}** · **MRR {quality.Mrr:F3}** · {quality.Cases} judged cases · " +
            $"{quality.CasesWithViolations} with forbidden retrievals · " +
            $"{(quality.Clean ? "✅ clean" : "⚠️ **see failures below**")}");
        sb.AppendLine();
        sb.AppendLine("| Category | Recall@K |");
        sb.AppendLine("|---|---:|");
        foreach (var (category, recall) in quality.RecallByCategory.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            sb.AppendLine(CultureInfo.InvariantCulture, $"| {category} | {recall:F3} |");
        sb.AppendLine();

        var imperfect = quality.CaseResults
            .Where(c => c.RecallAtK < 1.0 || c.ReciprocalRank < 1.0 || c.Violations.Count > 0)
            .ToList();
        if (imperfect.Count > 0)
        {
            sb.AppendLine("Cases not scoring perfectly — these are the rows a quality-risk change moves:");
            sb.AppendLine();
            sb.AppendLine("| Case | Kind | Recall@K | 1/rank | Retrieved | Forbidden retrieved |");
            sb.AppendLine("|---|---|---:|---:|---:|---|");
            foreach (var c in imperfect)
            {
                var violations = c.Violations.Count == 0 ? "–" : string.Join(", ", c.Violations);
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{c.CaseId}` | {c.Kind} | {c.RecallAtK:F2} | {c.ReciprocalRank:F2} | {c.Retrieved} | {violations} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("## Extraction quality (deterministic — scripted model, no judging model)");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"{extraction.Cases} judged cases · **false-positive rate {extraction.FalsePositiveRate:P0}** " +
            $"({extraction.FalsePositives}/{extraction.ExpectNothingCases} cases that should learn nothing) · " +
            $"{(extraction.Clean ? "✅ clean" : "⚠️ **see failures below**")}");
        sb.AppendLine();
        sb.AppendLine("| Kind | Precision | Recall |");
        sb.AppendLine("|---|---:|---:|");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| entities | {extraction.EntityPrecision:F3} | {extraction.EntityRecall:F3} |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| facts | {extraction.FactPrecision:F3} | {extraction.FactRecall:F3} |");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| preferences | {extraction.PreferencePrecision:F3} | {extraction.PreferenceRecall:F3} |");
        sb.AppendLine();

        var dirty = extraction.CaseResults.Where(c => !c.Clean).ToList();
        if (dirty.Count > 0)
        {
            sb.AppendLine("| Case | Missing | Unexpected | False positive |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var c in dirty)
            {
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{c.CaseId}` | {(c.Missing.Count == 0 ? "–" : string.Join(", ", c.Missing))} " +
                    $"| {(c.Unexpected.Count == 0 ? "–" : string.Join(", ", c.Unexpected))} " +
                    $"| {(c.FalsePositive ? "**YES**" : "–")} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();

        foreach (var group in measured.GroupBy(r => r.Scenario, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            sb.AppendLine(CultureInfo.InvariantCulture, $"## {group.Key}  ({group.Count()} measured iterations)");
            sb.AppendLine();
            sb.AppendLine("### Structural counters (exact — these are what CI may gate on)");
            sb.AppendLine();
            sb.AppendLine("Per iteration — i.e. per agent turn. Every row states its unit, because a bare");
            sb.AppendLine("count is ambiguous: 25 *write transactions* and 25 *milliseconds* are very");
            sb.AppendLine("different claims, and a performance record that cannot distinguish them is worse");
            sb.AppendLine("than no record. Where a counter has a matching span, its measured time is shown.");
            sb.AppendLine();
            sb.AppendLine("| Counter | Value | Unit | Total ms | Mean ms each | Deterministic |");
            sb.AppendLine("|---|---:|---|---:|---:|---|");
            foreach (var key in AllKeys(group))
            {
                var min = group.Min(r => r.Counter(key));
                var max = group.Max(r => r.Counter(key));
                var stable = min == max;
                var value = stable ? min.ToString(CultureInfo.InvariantCulture) : $"{min}–{max}";

                // Attach timing where a counter has a corresponding span, so "25 write transactions" is
                // immediately readable as "and they cost this much".
                var (totalMs, meanMs) = TimingFor(key, group);
                var totalText = totalMs is null ? "–" : totalMs.Value.ToString("F2", CultureInfo.InvariantCulture);
                var meanText = meanMs is null ? "–" : meanMs.Value.ToString("F2", CultureInfo.InvariantCulture);

                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{key}` | {value} | {UnitFor(key)} | {totalText} | {meanText} | {(stable ? "yes" : "**NO**")} |");
            }

            sb.AppendLine();
            sb.AppendLine(
                "Read and write transactions share one `memory.db.tx` span, so their **Total ms** and " +
                "**Mean ms each** are for all transactions combined, not for that access mode alone. " +
                "Totals are summed across concurrent work — see the timing note below.");

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
                $"| **iteration total** (elapsed) | 1 | {total.P50:F2} | {total.P95:F2} | {total.P50:F2} |");

            foreach (var name in AllSpans(group))
            {
                var summed = Percentiles(group.Select(r =>
                    r.SpanMilliseconds.TryGetValue(name, out var ms) ? ms : 0d).ToList());
                var occurrences = Median(group.Select(r =>
                    (double)(r.SpanCounts.TryGetValue(name, out var n) ? n : 0)).ToList());
                var perOccurrence = occurrences > 0 ? summed.P50 / occurrences : 0d;
                var note = name == "memory.db.query" ? " ⚠" : string.Empty;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{name}`{note} | {occurrences:F0} | {summed.P50:F2} | {summed.P95:F2} | {perOccurrence:F2} |");
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

    /// <summary>
    /// Latency distribution for one metric.
    /// </summary>
    /// <remarks>
    /// A record, deliberately, not a <c>ValueTuple</c>. <c>System.Text.Json</c> serializes public
    /// <em>properties</em>, and a tuple's members are fields — so returning a tuple here wrote
    /// <c>{}</c> for every timing in <c>summary.json</c> while <c>report.md</c>, which reads the values
    /// in C#, looked perfectly correct. The machine-readable artifact is the one that gates and trend
    /// analysis consume, so that silent hole was worse than a visible failure.
    /// </remarks>
    private sealed record Distribution(double P50, double P95, double Min, double Max);

    private static Distribution Percentiles(List<double> values)
    {
        if (values.Count == 0) return new Distribution(0, 0, 0, 0);
        values.Sort();
        return new Distribution(Quantile(values, 0.50), Quantile(values, 0.95), values[0], values[^1]);
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

    private static bool ParseDefaultTrue(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        return value.ToLowerInvariant() switch
        {
            "true" or "1" or "on" or "yes" => true,
            "false" or "0" or "off" or "no" => false,
            _ => throw new ArgumentException($"--{name} must be true or false."),
        };
    }

    /// <summary>Keeps the label safe for a directory name without silently mangling it.</summary>
    private static string? Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var invalid = Path.GetInvalidFileNameChars().Concat(['_', ' ']).ToArray();
        return new string(label.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }
}
