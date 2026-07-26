using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.Cli.Perf;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Runs control and candidate configurations alternately in one process against one hermetic profile.
/// Structural counters remain exact; timings are compared only as paired within-run ratios.
/// </summary>
public sealed class PerfAbCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly TextWriter _output;

    public PerfAbCommand(TextWriter output) => _output = output;

    public async Task<int> ExecuteAsync(
        string? controlValue,
        string? candidateValue,
        string? scenarioFilter,
        string? iterationsValue,
        string? warmupValue,
        string? dimensionsValue,
        string? latency,
        string? outputRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(controlValue) || string.IsNullOrWhiteSpace(candidateValue))
        {
            _output.WriteLine("error: perf ab requires --control <spec> and --candidate <spec>.");
            return 1;
        }

        PerfConfiguration control;
        PerfConfiguration candidate;
        IReadOnlyList<PerfScenario> scenarios;
        try
        {
            control = PerfConfiguration.Parse(controlValue);
            candidate = PerfConfiguration.Parse(candidateValue);
            scenarios = PerfScenarios.Select(scenarioFilter ?? "PERF-R-04");
            var unsupported = scenarios.Where(s => !s.SupportsInterleavedAb).Select(s => s.Id).ToList();
            if (unsupported.Count > 0)
            {
                throw new ArgumentException(
                    $"interleaved A/B does not support state-mutating scenarios ({string.Join(", ", unsupported)}); " +
                    "their shared database state violates paired-sample independence.");
            }
        }
        catch (ArgumentException ex)
        {
            _output.WriteLine($"error: {ex.Message}");
            return 1;
        }

        var iterations = ParsePositive(iterationsValue, 30, "iterations");
        if (iterations < 12 || iterations % 6 != 0)
        {
            _output.WriteLine("error: --iterations must be a multiple of six and at least 12 for block inference.");
            return 1;
        }
        var warmup = ParseNonNegative(warmupValue, 3, "warmup");
        var dimensions = ParsePositive(dimensionsValue, 384, "embedding-dimensions");
        var (embeddingLatency, modelLatency, latencyName) = ResolveLatency(latency);

        var startedAt = DateTimeOffset.UtcNow;
        var runId = $"{startedAt:yyyyMMdd'T'HHmmss'Z'}__ab__hermetic-S-{latencyName}";
        var runDir = Path.Combine(outputRoot ?? Path.Combine("artifacts", "perf"), runId);
        Directory.CreateDirectory(runDir);

        var manifest = new
        {
            runId,
            mode = "ab",
            startedAtUtc = startedAt,
            control = control.CanonicalSpec,
            candidate = candidate.CanonicalSpec,
            scenarios = scenarios.Select(s => s.Id).ToArray(),
            iterations,
            warmup,
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

        _output.WriteLine($"perf ab: run {runId}");
        _output.WriteLine($"perf ab: control   {control.CanonicalSpec}");
        _output.WriteLine($"perf ab: candidate {candidate.CanonicalSpec}");

        await File.WriteAllTextAsync(
            Path.Combine(runDir, "run.json"),
            JsonSerializer.Serialize(manifest, Json),
            cancellationToken).ConfigureAwait(false);

        using var trace = new TraceLogWriter(Path.Combine(runDir, "trace.ndjson"));
        trace.RunStart(runId, manifest);
        var runStopwatch = Stopwatch.StartNew();
        using var collector = new PerfCollector(trace);

        var extractionFixture = ExtractionQualityFixture.Load();
        var scriptedRules = extractionFixture.ScriptedRules().Concat(PerfScenarios.ScriptedRules).ToList();

        await using var profile = await HermeticProfile
            .StartAsync(
                dimensions,
                embeddingLatency,
                modelLatency,
                _output,
                scriptedRules,
                cancellationToken)
            .ConfigureAwait(false);

        await PerfFixture.SeedAsync(
            profile, _output, cancellationToken, PerfFixture.ForVariant("control")).ConfigureAwait(false);
        await PerfFixture.SeedAsync(
            profile, _output, cancellationToken, PerfFixture.ForVariant("candidate")).ConfigureAwait(false);

        var qualityFixture = QualityFixture.Load();
        var qualitySeeder = new QualityEvaluator(qualityFixture, profile.Services, dimensions);
        await qualitySeeder.SeedAsync(_output, cancellationToken).ConfigureAwait(false);

        var controlProvider = PerfScenarios.CreateProvider(profile, control.Recall);
        var candidateProvider = PerfScenarios.CreateProvider(profile, candidate.Recall);
        var samples = new List<AbSample>();

        try
        {
            foreach (var scenario in scenarios)
            {
                _output.WriteLine($"perf ab: {scenario.Id} — counterbalanced AB/BA");
                for (var i = 0; i < warmup; i++)
                {
                    await RunPairAsync(
                        scenario, control, candidate, controlProvider, candidateProvider,
                        profile, collector, i, "warmup", samples, cancellationToken).ConfigureAwait(false);
                }

                for (var i = 0; i < iterations; i++)
                {
                    await RunPairAsync(
                        scenario, control, candidate, controlProvider, candidateProvider,
                        profile, collector, i, "measure", samples, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex)
        {
            runStopwatch.Stop();
            trace.RunEnd(collector.Records.Count, runStopwatch.Elapsed.TotalMilliseconds);
            _output.WriteLine($"error: A/B scenario failed: {ex.Message}");
            return 1;
        }

        _output.WriteLine("perf ab: scoring control and candidate retrieval quality…");
        var controlQuality = await new QualityEvaluator(
                qualityFixture, profile.Services, dimensions, control.Recall)
            .EvaluateAsync(cancellationToken).ConfigureAwait(false);
        var candidateQuality = await new QualityEvaluator(
                qualityFixture, profile.Services, dimensions, candidate.Recall)
            .EvaluateAsync(cancellationToken).ConfigureAwait(false);

        _output.WriteLine("perf ab: scoring shared extraction quality…");
        var extractionQuality = await new ExtractionQualityEvaluator(
                extractionFixture, profile.Services, profile.Driver)
            .EvaluateAsync(cancellationToken).ConfigureAwait(false);

        var baseline = QualityGate.LoadBaseline();
        var controlGate = QualityGate.Evaluate(
            baseline, controlQuality, extractionQuality, QualityGate.DefaultBaselinePath);
        var candidateGate = QualityGate.Evaluate(
            baseline, candidateQuality, extractionQuality, QualityGate.DefaultBaselinePath);

        runStopwatch.Stop();
        trace.RunEnd(collector.Records.Count, runStopwatch.Elapsed.TotalMilliseconds);

        var measured = samples.Where(s => s.Record.Phase == "measure").ToList();
        var comparisons = BuildComparisons(measured, scenarios);
        var nullViolations = ValidateNullExperiment(control, candidate, comparisons);

        await WriteSamplesAsync(runDir, measured, cancellationToken).ConfigureAwait(false);
        var summary = new
        {
            manifest,
            quality = new
            {
                control = controlQuality,
                candidate = candidateQuality,
                extractionShared = extractionQuality,
                controlGate,
                candidateGate,
            },
            scenarios = comparisons,
            nullExperiment = new
            {
                applicable = control.Recall == candidate.Recall,
                passed = nullViolations.Count == 0,
                violations = nullViolations,
            },
        };
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "summary.json"),
            JsonSerializer.Serialize(summary, Json),
            cancellationToken).ConfigureAwait(false);

        var report = RenderReport(
            runId,
            control,
            candidate,
            comparisons,
            controlQuality,
            candidateQuality,
            extractionQuality,
            controlGate,
            candidateGate,
            nullViolations);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "report.md"), report, cancellationToken).ConfigureAwait(false);

        _output.WriteLine();
        _output.Write(report);
        _output.WriteLine($"perf ab: wrote {runDir}");

        if (nullViolations.Count == 0)
            return 0;

        foreach (var violation in nullViolations)
            _output.WriteLine($"error: {violation}");
        return 1;
    }

    private static async Task RunPairAsync(
        PerfScenario scenario,
        PerfConfiguration control,
        PerfConfiguration candidate,
        AgentMemory.AgentFramework.Neo4jMemoryContextProvider controlProvider,
        AgentMemory.AgentFramework.Neo4jMemoryContextProvider candidateProvider,
        HermeticProfile profile,
        PerfCollector collector,
        int iteration,
        string phase,
        ICollection<AbSample> samples,
        CancellationToken cancellationToken)
    {
        if (iteration % 2 != 0)
        {
            await RunSideAsync(
                scenario, "candidate", candidate, candidateProvider, profile, collector,
                iteration, phase, samples, cancellationToken).ConfigureAwait(false);
            await RunSideAsync(
                scenario, "control", control, controlProvider, profile, collector,
                iteration, phase, samples, cancellationToken).ConfigureAwait(false);
            return;
        }

        await RunSideAsync(
            scenario, "control", control, controlProvider, profile, collector,
            iteration, phase, samples, cancellationToken).ConfigureAwait(false);
        await RunSideAsync(
            scenario, "candidate", candidate, candidateProvider, profile, collector,
            iteration, phase, samples, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunSideAsync(
        PerfScenario scenario,
        string variant,
        PerfConfiguration configuration,
        AgentMemory.AgentFramework.Neo4jMemoryContextProvider provider,
        HermeticProfile profile,
        PerfCollector collector,
        int iteration,
        string phase,
        ICollection<AbSample> samples,
        CancellationToken cancellationToken)
    {
        var datasetVariant = DatasetVariantFor(variant, iteration);
        await scenario.PrepareAsync(new ScenarioSetupContext(
            profile,
            iteration,
            phase,
            datasetVariant,
            cancellationToken)).ConfigureAwait(false);

        TurnRecord record;
        using (var turn = collector.BeginTurn($"{scenario.Id}/{variant}", iteration, phase))
        {
            record = turn.Record;
            await scenario.ExecuteAsync(new ScenarioContext(
                profile,
                provider,
                record,
                iteration,
                phase,
                datasetVariant,
                configuration.Recall,
                cancellationToken)).ConfigureAwait(false);
        }

        await scenario.ValidateAsync(new ScenarioVerificationContext(
            profile, record, iteration, phase, datasetVariant, cancellationToken)).ConfigureAwait(false);
        samples.Add(new AbSample(scenario.Id, variant, record));
    }

    internal static string DatasetVariantFor(string configurationVariant, int iteration) =>
        iteration % 2 == 0
            ? configurationVariant
            : configurationVariant switch
            {
                "control" => "candidate",
                "candidate" => "control",
                _ => throw new ArgumentException($"unknown configuration variant '{configurationVariant}'."),
            };

    private static IReadOnlyList<ScenarioComparison> BuildComparisons(
        IReadOnlyList<AbSample> samples,
        IReadOnlyList<PerfScenario> scenarios)
    {
        var results = new List<ScenarioComparison>();
        foreach (var scenario in scenarios)
        {
            var control = samples
                .Where(s => s.Scenario == scenario.Id && s.Variant == "control")
                .OrderBy(s => s.Record.Iteration)
                .Select(s => s.Record)
                .ToList();
            var candidate = samples
                .Where(s => s.Scenario == scenario.Id && s.Variant == "candidate")
                .OrderBy(s => s.Record.Iteration)
                .Select(s => s.Record)
                .ToList();

            var counterNames = control
                .SelectMany(r => r.Counters.Keys)
                .Concat(candidate.SelectMany(r => r.Counters.Keys))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);
            var counters = counterNames.Select(name => new CounterComparison(
                name,
                Values(control, name),
                Values(candidate, name))).ToList();

            var timings = new List<TimingComparison>
            {
                Timing("iteration total", control.Select(r => r.DurationMs), candidate.Select(r => r.DurationMs)),
            };

            var spanNames = control
                .SelectMany(r => r.SpanMilliseconds.Keys)
                .Concat(candidate.SelectMany(r => r.SpanMilliseconds.Keys))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);
            foreach (var spanName in spanNames)
            {
                var controlValues = control.Select(r => r.SpanMilliseconds.GetValueOrDefault(spanName)).ToList();
                var candidateValues = candidate.Select(r => r.SpanMilliseconds.GetValueOrDefault(spanName)).ToList();
                if (controlValues.All(v => v > 0) && candidateValues.All(v => v > 0))
                    timings.Add(Timing(spanName, controlValues, candidateValues));
            }

            results.Add(new ScenarioComparison(scenario.Id, control.Count, counters, timings));
        }

        return results;
    }

    private static TimingComparison Timing(
        string metric,
        IEnumerable<double> controlValues,
        IEnumerable<double> candidateValues)
    {
        var control = controlValues.ToList();
        var candidate = candidateValues.ToList();
        return new TimingComparison(
            metric,
            Median(control),
            Median(candidate),
            PairedRatioBootstrap.AnalyzeCounterbalanced(control, candidate));
    }

    private static ExactValues Values(IReadOnlyList<TurnRecord> records, string name)
    {
        var values = records.Select(r => r.Counter(name)).ToList();
        return new ExactValues(values.Min(), values.Max());
    }

    private static IReadOnlyList<string> ValidateNullExperiment(
        PerfConfiguration control,
        PerfConfiguration candidate,
        IReadOnlyList<ScenarioComparison> comparisons)
    {
        if (control.Recall != candidate.Recall)
            return [];

        var violations = new List<string>();
        foreach (var scenario in comparisons)
        {
            foreach (var counter in scenario.Counters)
            {
                if (counter.Control != counter.Candidate)
                {
                    violations.Add(
                        $"{scenario.Scenario} identical configurations changed counter {counter.Name}: " +
                        $"{counter.Control.Display} vs {counter.Candidate.Display}.");
                }
            }

            var total = scenario.Timings.Single(t => t.Metric == "iteration total");
            if (total.Ratio.Verdict != TimingVerdict.NoSignificantDifference)
            {
                violations.Add(
                    $"{scenario.Scenario} identical configurations reported " +
                    $"{VerdictText(total.Ratio.Verdict)} for iteration total; " +
                    $"95% CI {total.Ratio.Lower95:F3}–{total.Ratio.Upper95:F3} must include 1.0.");
            }
        }

        return violations;
    }

    private static async Task WriteSamplesAsync(
        string runDir,
        IReadOnlyList<AbSample> samples,
        CancellationToken cancellationToken)
    {
        var lines = samples.Select(sample => JsonSerializer.Serialize(new
        {
            scenario = sample.Scenario,
            variant = sample.Variant,
            iteration = sample.Record.Iteration,
            durMs = sample.Record.DurationMs,
            counters = sample.Record.Counters,
            spansMs = sample.Record.SpanMilliseconds,
        }));
        await File.WriteAllLinesAsync(
            Path.Combine(runDir, "samples.ndjson"), lines, cancellationToken).ConfigureAwait(false);
    }

    private static string RenderReport(
        string runId,
        PerfConfiguration control,
        PerfConfiguration candidate,
        IReadOnlyList<ScenarioComparison> comparisons,
        QualityResult controlQuality,
        QualityResult candidateQuality,
        ExtractionQualityResult extraction,
        QualityGateResult controlGate,
        QualityGateResult candidateGate,
        IReadOnlyList<string> nullViolations)
    {
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"## Interleaved A/B — `{runId}`");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Control: `{control.CanonicalSpec}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- Candidate: `{candidate.CanonicalSpec}`");
        sb.AppendLine("- Schedule: counterbalanced adjacent pairs `AB, BA, AB, BA…` in one process/database");
        sb.AppendLine(
            "- Timing inference: six-pair (three `AB/BA` cycle) blocks are bootstrapped as independent units");
        sb.AppendLine("- Dataset crossover: each configuration runs equally against both disjoint fixture copies");
        sb.AppendLine();

        if (control.Recall == candidate.Recall)
        {
            sb.AppendLine(nullViolations.Count == 0
                ? "**Null experiment: PASS — no significant difference.**"
                : "**Null experiment: FAIL.**");
            if (nullViolations.Count > 0)
            {
                foreach (var violation in nullViolations)
                    sb.AppendLine($"- {violation}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("### Quality");
        sb.AppendLine();
        sb.AppendLine("| Metric | Control | Candidate | Delta |");
        sb.AppendLine("|---|---:|---:|---:|");
        QualityRow(sb, "Retrieval Recall@K", controlQuality.RecallAtK, candidateQuality.RecallAtK);
        QualityRow(sb, "Retrieval MRR", controlQuality.Mrr, candidateQuality.Mrr);
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Forbidden-retrieval cases | {controlQuality.CasesWithViolations} " +
            $"| {candidateQuality.CasesWithViolations} " +
            $"| {candidateQuality.CasesWithViolations - controlQuality.CasesWithViolations:+#;-#;0} |");
        QualityRow(sb, "Extraction entity precision (shared)", extraction.EntityPrecision, extraction.EntityPrecision);
        QualityRow(sb, "Extraction entity recall (shared)", extraction.EntityRecall, extraction.EntityRecall);
        QualityRow(sb, "Extraction fact precision (shared)", extraction.FactPrecision, extraction.FactPrecision);
        QualityRow(sb, "Extraction fact recall (shared)", extraction.FactRecall, extraction.FactRecall);
        QualityRow(sb, "Extraction preference precision (shared)", extraction.PreferencePrecision, extraction.PreferencePrecision);
        QualityRow(sb, "Extraction preference recall (shared)", extraction.PreferenceRecall, extraction.PreferenceRecall);
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| Extraction false-positive rate (shared) | {extraction.FalsePositiveRate:F3} " +
            $"| {extraction.FalsePositiveRate:F3} | 0.000 |");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"Quality baseline: control **{(controlGate.Passed ? "PASS" : "FAIL")}**, " +
            $"candidate **{(candidateGate.Passed ? "PASS" : "FAIL")}**.");
        if (!candidateGate.Passed)
        {
            foreach (var violation in candidateGate.Violations)
                sb.AppendLine($"- Candidate: {violation}");
        }
        sb.AppendLine();

        foreach (var scenario in comparisons)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"### {scenario.Scenario} ({scenario.Pairs} paired iterations)");
            sb.AppendLine();
            sb.AppendLine("#### Structural counters");
            sb.AppendLine();
            sb.AppendLine("| Counter | Control | Candidate | Delta | Deterministic |");
            sb.AppendLine("|---|---:|---:|---:|---|");
            foreach (var counter in scenario.Counters)
            {
                var delta = counter.Control.Deterministic && counter.Candidate.Deterministic
                    ? (counter.Candidate.Min - counter.Control.Min)
                        .ToString("+#;-#;0", CultureInfo.InvariantCulture)
                    : "–";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{counter.Name}` | {counter.Control.Display} | {counter.Candidate.Display} " +
                    $"| {delta} | {(counter.Control.Deterministic && counter.Candidate.Deterministic ? "yes" : "**NO**")} |");
            }
            sb.AppendLine();

            sb.AppendLine("#### Paired timings — candidate/control");
            sb.AppendLine();
            sb.AppendLine("| Metric | Control p50 ms | Candidate p50 ms | Ratio | Bootstrap 95% CI | Verdict |");
            sb.AppendLine("|---|---:|---:|---:|---:|---|");
            foreach (var timing in scenario.Timings)
            {
                var verdict = timing.Metric == "iteration total"
                    ? VerdictText(timing.Ratio.Verdict)
                    : "exploratory";
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"| `{timing.Metric}` | {timing.ControlP50Ms:F2} | {timing.CandidateP50Ms:F2} " +
                    $"| {timing.Ratio.Estimate:F3} | {timing.Ratio.Lower95:F3}–{timing.Ratio.Upper95:F3} " +
                    $"| {verdict} |");
            }
            sb.AppendLine();
        }

        sb.AppendLine(
            "Iteration total is the pre-registered headline: a win is declared only when its confidence " +
            "interval’s upper bound is below 1.0. Subspan intervals are exploratory and are not corrected " +
            "for multiple comparisons. " +
            "These timings are meaningful only within this interleaved run; structural counters are portable.");
        return sb.ToString();
    }

    private static void QualityRow(StringBuilder sb, string label, double control, double candidate) =>
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| {label} | {control:F3} | {candidate:F3} | {candidate - control:+0.000;-0.000;0.000} |");

    private static string VerdictText(TimingVerdict verdict) => verdict switch
    {
        TimingVerdict.Improvement => "**improvement**",
        TimingVerdict.Regression => "**regression**",
        _ => "no significant difference",
    };

    private static double Median(List<double> values)
    {
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static int ParsePositive(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed <= 0)
        {
            throw new ArgumentException($"--{name} must be a positive integer.");
        }

        return parsed;
    }

    private static int ParseNonNegative(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0)
        {
            throw new ArgumentException($"--{name} must be zero or a positive integer.");
        }

        return parsed;
    }

    private static (TimeSpan Embedding, TimeSpan Model, string Name) ResolveLatency(string? latency) =>
        latency?.ToLowerInvariant() switch
        {
            null or "zero" => (TimeSpan.Zero, TimeSpan.Zero, "zero"),
            "remote" => (TimeSpan.FromMilliseconds(120), TimeSpan.FromMilliseconds(900), "remote"),
            _ => throw new ArgumentException($"unknown --latency '{latency}'. Use 'zero' or 'remote'."),
        };

    private static string? TryGetCommit()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (process is null) return null;
            var stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5_000);
            return process.ExitCode == 0 ? stdout.Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private sealed record AbSample(string Scenario, string Variant, TurnRecord Record);

    private sealed record ExactValues(long Min, long Max)
    {
        public bool Deterministic => Min == Max;
        public string Display => Deterministic
            ? Min.ToString(CultureInfo.InvariantCulture)
            : $"{Min.ToString(CultureInfo.InvariantCulture)}–{Max.ToString(CultureInfo.InvariantCulture)}";
    }

    private sealed record CounterComparison(string Name, ExactValues Control, ExactValues Candidate);

    private sealed record TimingComparison(
        string Metric,
        double ControlP50Ms,
        double CandidateP50Ms,
        PairedRatioResult Ratio);

    private sealed record ScenarioComparison(
        string Scenario,
        int Pairs,
        IReadOnlyList<CounterComparison> Counters,
        IReadOnlyList<TimingComparison> Timings);
}
