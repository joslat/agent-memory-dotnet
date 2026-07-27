using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AgentMemory.Cli.Perf;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Runs cold samples in separate child processes, runs a normal warm reference, then merges their
/// immutable artifacts without ever mixing the two timing populations.
/// </summary>
public sealed class PerfColdCommand
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly TextWriter _output;

    public PerfColdCommand(TextWriter output) => _output = output;

    public async Task<int> ExecuteAsync(
        string? label,
        string? scenarioFilter,
        string? samplesValue,
        string? warmupValue,
        string? dimensionsValue,
        string? scaleValue,
        string? latencyValue,
        string? outputRoot,
        CancellationToken cancellationToken = default)
    {
        var labelValue = Sanitize(label) ?? "cold-warm";
        var sampleCount = ParsePositive(samplesValue, 5, "samples");
        var warmup = ParseNonNegative(warmupValue, 3, "warmup");
        var dimensions = ParsePositive(dimensionsValue, 384, "embedding-dimensions");
        var scale = PerfScaleParser.Parse(scaleValue).Name();
        var latency = ParseLatency(latencyValue);
        var scenario = SelectOneScenario(scenarioFilter);

        var startedAt = DateTimeOffset.UtcNow;
        var runId =
            $"{startedAt:yyyyMMdd'T'HHmmss'Z'}__{labelValue}__cold-warm-{scale}-{latency}";
        var runDir = Path.GetFullPath(Path.Combine(
            outputRoot ?? Path.Combine("artifacts", "perf"),
            runId));
        var childRoot = Path.Combine(runDir, "children");
        Directory.CreateDirectory(childRoot);

        _output.WriteLine($"perf cold: run {runId}");
        var coldSamples = new List<ColdWarmSample>(sampleCount);
        var childRuns = new List<string>(sampleCount + 1);

        for (var i = 0; i < sampleCount; i++)
        {
            _output.WriteLine($"perf cold: cold child {i + 1}/{sampleCount}…");
            var output = Path.Combine(childRoot, $"cold-{i + 1:D3}");
            var childRun = await RunChildAsync(
                output,
                [
                    "perf", "run",
                    "--label", $"{labelValue}-cold-{i + 1:D3}",
                    "--scenarios", scenario.Id,
                    "--iterations", "1",
                    "--warmup", "0",
                    "--embedding-dimensions", dimensions.ToString(CultureInfo.InvariantCulture),
                    "--scale", scale,
                    "--latency", latency,
                    "--output", output,
                    "--quality-gate", "false",
                    "--single-shot", "true",
                ],
                cancellationToken).ConfigureAwait(false);
            childRuns.Add(childRun);

            var loaded = await LoadSamplesAsync(
                childRun, scenario.Id, scale, latency, cancellationToken).ConfigureAwait(false);
            if (loaded.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Cold child {i + 1} wrote {loaded.Count} measured samples; expected exactly one.");
            }
            coldSamples.Add(loaded.Single());
        }

        _output.WriteLine(
            $"perf cold: warm reference ({warmup} warm-up + {sampleCount} measured)…");
        var warmOutput = Path.Combine(childRoot, "warm");
        var warmRun = await RunChildAsync(
            warmOutput,
            [
                "perf", "run",
                "--label", $"{labelValue}-warm",
                "--scenarios", scenario.Id,
                "--iterations", sampleCount.ToString(CultureInfo.InvariantCulture),
                "--warmup", warmup.ToString(CultureInfo.InvariantCulture),
                "--embedding-dimensions", dimensions.ToString(CultureInfo.InvariantCulture),
                "--scale", scale,
                "--latency", latency,
                "--output", warmOutput,
                "--quality-gate", "true",
            ],
            cancellationToken).ConfigureAwait(false);
        childRuns.Add(warmRun);

        var warmSamples = await LoadSamplesAsync(
            warmRun, scenario.Id, scale, latency, cancellationToken).ConfigureAwait(false);
        if (warmSamples.Count != sampleCount)
        {
            throw new InvalidOperationException(
                $"Warm child wrote {warmSamples.Count} measured samples; expected {sampleCount}.");
        }

        var analysis = ColdWarmAnalyzer.Analyze(coldSamples, warmSamples);
        var reset = PerfCacheResetManifest.ColdSingleShot;
        var manifest = new
        {
            runId,
            label = labelValue,
            startedAtUtc = startedAt,
            mode = "cold-warm",
            profile = "hermetic",
            scale,
            latency,
            scenario = scenario.Id,
            coldSamples = sampleCount,
            warmSamples = sampleCount,
            warmup,
            embeddingDimensions = dimensions,
            cacheReset = reset,
            childRuns = childRuns.Select(path => Path.GetRelativePath(runDir, path)).ToArray(),
        };

        await File.WriteAllTextAsync(
            Path.Combine(runDir, "run.json"),
            JsonSerializer.Serialize(manifest, Json),
            cancellationToken).ConfigureAwait(false);
        await WriteMergedSamplesAsync(
            runDir, coldSamples, warmSamples, childRuns, cancellationToken).ConfigureAwait(false);
        await MergeTraceFragmentsAsync(runDir, childRuns, cancellationToken).ConfigureAwait(false);
        await WriteSummaryAsync(
            runDir, manifest, analysis, cancellationToken).ConfigureAwait(false);
        var report = RenderReport(runId, analysis, reset);
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "report.md"), report, cancellationToken).ConfigureAwait(false);

        _output.WriteLine();
        _output.Write(report);
        _output.WriteLine($"perf cold: wrote {runDir}");
        return 0;
    }

    private static PerfScenario SelectOneScenario(string? filter)
    {
        var selected = PerfScenarios.Select(
            string.IsNullOrWhiteSpace(filter) ? "PERF-R-04" : filter).ToList();
        if (selected.Count != 1)
        {
            throw new ArgumentException(
                "`perf cold` requires exactly one scenario because only the first scenario in a " +
                "process can be a cold sample.");
        }
        return selected.Single();
    }

    private async Task<string> RunChildAsync(
        string outputRoot,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputRoot);
        var startInfo = BuildCurrentProcessStartInfo(arguments);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start perf child process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Perf child exited {process.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{Tail(stdout)}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{Tail(stderr)}");
        }

        var runDirectories = Directory.GetDirectories(outputRoot);
        if (runDirectories.Length != 1)
        {
            throw new InvalidOperationException(
                $"Perf child wrote {runDirectories.Length} run directories under '{outputRoot}'; expected one.");
        }
        return Path.GetFullPath(runDirectories.Single());
    }

    internal static ProcessStartInfo BuildCurrentProcessStartInfo(IReadOnlyList<string> arguments)
    {
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("Current process path is unavailable.");
        var info = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Environment.CurrentDirectory,
        };

        if (string.Equals(
                Path.GetFileNameWithoutExtension(executable),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assembly))
                throw new InvalidOperationException("Entry assembly path is unavailable.");
            info.ArgumentList.Add(assembly);
        }

        foreach (var argument in arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static async Task<IReadOnlyList<ColdWarmSample>> LoadSamplesAsync(
        string runDir,
        string expectedScenario,
        string expectedScale,
        string expectedLatency,
        CancellationToken cancellationToken)
    {
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(
                Path.Combine(runDir, "run.json"), cancellationToken).ConfigureAwait(false));
        var root = manifest.RootElement;
        var profile = root.GetProperty("profile").GetString()
            ?? throw new InvalidOperationException("Child manifest profile is null.");
        var scale = root.GetProperty("scale").GetString()
            ?? throw new InvalidOperationException("Child manifest scale is null.");
        var environment = root.GetProperty("environment");
        var embeddingLatency = environment.GetProperty("embeddingLatencyMs").GetDouble();
        var modelLatency = environment.GetProperty("modelLatencyMs").GetDouble();
        var latency = embeddingLatency == 0 && modelLatency == 0
            ? "zero"
            : embeddingLatency == 120 && modelLatency == 900
                ? "remote"
                : $"custom:{embeddingLatency}:{modelLatency}";

        if (!string.Equals(profile, "hermetic", StringComparison.Ordinal) ||
            !string.Equals(scale, expectedScale, StringComparison.Ordinal) ||
            !string.Equals(latency, expectedLatency, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Child manifest fingerprint mismatch: {profile}/{scale}/{latency}; " +
                $"expected hermetic/{expectedScale}/{expectedLatency}.");
        }

        var lines = await File.ReadAllLinesAsync(
            Path.Combine(runDir, "samples.ndjson"), cancellationToken).ConfigureAwait(false);
        var samples = new List<ColdWarmSample>(lines.Length);
        foreach (var line in lines)
        {
            using var document = JsonDocument.Parse(line);
            var sample = document.RootElement;
            var scenario = sample.GetProperty("scenario").GetString()
                ?? throw new InvalidOperationException("Sample scenario is null.");
            if (!string.Equals(scenario, expectedScenario, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Child sample scenario '{scenario}' does not match '{expectedScenario}'.");
            }

            samples.Add(new ColdWarmSample(
                scenario,
                sample.GetProperty("durMs").GetDouble(),
                ReadLongMap(sample.GetProperty("counters")),
                ReadLongMap(sample.GetProperty("queryFingerprints")),
                profile,
                scale,
                latency));
        }
        return samples;
    }

    private static IReadOnlyDictionary<string, long> ReadLongMap(JsonElement element) =>
        element.EnumerateObject().ToDictionary(
            property => property.Name,
            property => property.Value.GetInt64(),
            StringComparer.Ordinal);

    private static async Task WriteMergedSamplesAsync(
        string runDir,
        IReadOnlyList<ColdWarmSample> cold,
        IReadOnlyList<ColdWarmSample> warm,
        IReadOnlyList<string> childRuns,
        CancellationToken cancellationToken)
    {
        var lines = cold.Select((sample, index) => JsonSerializer.Serialize(new
            {
                population = "cold",
                order = index + 1,
                durationMs = sample.DurationMs,
                counters = sample.Counters,
                queryFingerprints = sample.QueryFingerprints,
                sourceRun = Path.GetRelativePath(runDir, childRuns[index]),
            }))
            .Concat(warm.Select((sample, index) => JsonSerializer.Serialize(new
            {
                population = "warm",
                order = index + 1,
                durationMs = sample.DurationMs,
                counters = sample.Counters,
                queryFingerprints = sample.QueryFingerprints,
                sourceRun = Path.GetRelativePath(runDir, childRuns[^1]),
            })));
        await File.WriteAllLinesAsync(
            Path.Combine(runDir, "samples.ndjson"), lines, cancellationToken).ConfigureAwait(false);
    }

    private static async Task MergeTraceFragmentsAsync(
        string runDir,
        IReadOnlyList<string> childRuns,
        CancellationToken cancellationToken)
    {
        await using var output = new StreamWriter(Path.Combine(runDir, "trace.ndjson"), false);
        foreach (var childRun in childRuns)
        {
            var lines = await File.ReadAllLinesAsync(
                Path.Combine(childRun, "trace.ndjson"), cancellationToken).ConfigureAwait(false);
            foreach (var line in lines)
                await output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WriteSummaryAsync(
        string runDir,
        object manifest,
        ColdWarmAnalysis analysis,
        CancellationToken cancellationToken)
    {
        var summary = new
        {
            manifest,
            cold = new
            {
                orderedMilliseconds = analysis.ColdMilliseconds,
                medianMs = analysis.ColdMedianMs,
            },
            warm = new
            {
                milliseconds = analysis.WarmMilliseconds,
                medianMs = analysis.WarmMedianMs,
            },
            coldPenaltyRatio = analysis.ColdPenaltyRatio,
            structuralCounters = analysis.StructuralCounters,
            queryFingerprints = analysis.QueryFingerprints,
        };
        await File.WriteAllTextAsync(
            Path.Combine(runDir, "summary.json"),
            JsonSerializer.Serialize(summary, Json),
            cancellationToken).ConfigureAwait(false);
    }

    private static string RenderReport(
        string runId,
        ColdWarmAnalysis analysis,
        PerfCacheResetManifest reset)
    {
        var coldOrdered = string.Join(
            ", ",
            analysis.ColdMilliseconds.Select((value, index) =>
                $"{index + 1}: {value.ToString("F3", CultureInfo.InvariantCulture)}"));
        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Cold vs warm — {runId}");
        sb.AppendLine();
        sb.AppendLine("| Scenario | Cold samples, ordered (ms) | Cold median (ms) | Warm median (ms) | Cold penalty |");
        sb.AppendLine("|---|---|---:|---:|---:|");
        sb.AppendLine(CultureInfo.InvariantCulture,
            $"| `{analysis.Scenario}` | {coldOrdered} | {analysis.ColdMedianMs:F3} | " +
            $"{analysis.WarmMedianMs:F3} | {analysis.ColdPenaltyRatio:F3}× |");
        sb.AppendLine();
        sb.AppendLine("Cold values are separate fresh-process observations and are never included in warm percentiles.");
        sb.AppendLine("All structural counters and safe query fingerprints matched across both populations. ✅");
        sb.AppendLine();
        sb.AppendLine("## Cache reset record");
        sb.AppendLine();
        sb.AppendLine($"- Process: {reset.Process}");
        sb.AppendLine($"- JIT: {reset.Jit}");
        sb.AppendLine($"- Service provider: {reset.ServiceProvider}");
        sb.AppendLine($"- Driver pool: {reset.DriverConnectionPool}");
        sb.AppendLine($"- Neo4j query-plan cache: {reset.Neo4jQueryPlanCache}");
        sb.AppendLine($"- Neo4j page cache: {reset.Neo4jPageCache}");
        sb.AppendLine($"- OS filesystem cache: {reset.OsFilesystemCache}");
        sb.AppendLine();
        sb.AppendLine("Timings describe this hermetic run only; they are not deployment-performance claims.");
        return sb.ToString();
    }

    private static string Tail(string text) =>
        text.Length <= 4_000 ? text : text[^4_000..];

    private static string ParseLatency(string? value) =>
        value?.ToLowerInvariant() switch
        {
            null or "zero" => "zero",
            "remote" => "remote",
            _ => throw new ArgumentException($"unknown --latency '{value}'. Use 'zero' or 'remote'."),
        };

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

    private static string? Sanitize(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var invalid = Path.GetInvalidFileNameChars().Concat(['_', ' ']).ToArray();
        return new string(label.Select(c => invalid.Contains(c) ? '-' : c).ToArray());
    }
}
