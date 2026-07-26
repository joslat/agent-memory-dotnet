using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Cli.Perf;

internal sealed record PerfBaselineDocument(
    int SchemaVersion,
    string Profile,
    double QualityTolerance,
    IReadOnlyDictionary<string, PerfScenarioBaseline> Scenarios,
    PerfQualityBaseline Quality)
{
    public const string DefaultPath = "eng/perf/baselines/hermetic-S.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static PerfBaselineDocument FromSummary(JsonElement summary)
    {
        var manifest = Required(summary, "manifest");
        var profile = Required(manifest, "profile").GetString();
        var scale = Required(manifest, "scale").GetString();
        if (profile != "hermetic" || scale != "S")
        {
            throw new InvalidDataException(
                $"performance baselines require profile hermetic, scale S; got {profile ?? "(null)"}, " +
                $"{scale ?? "(null)"}.");
        }

        var scenarios = new SortedDictionary<string, PerfScenarioBaseline>(StringComparer.Ordinal);
        foreach (var scenario in Required(summary, "scenarios").EnumerateArray())
        {
            var id = Required(scenario, "scenario").GetString()
                ?? throw new InvalidDataException("scenario id must be a string.");
            var counters = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var counter in Required(scenario, "counters").EnumerateObject())
            {
                var value = counter.Value;
                var minimum = Required(value, "min").GetInt64();
                var maximum = Required(value, "max").GetInt64();
                var deterministic = Required(value, "deterministic").GetBoolean();
                if (!deterministic || minimum != maximum)
                {
                    throw new InvalidDataException(
                        $"{id} counter {counter.Name} must be deterministic to enter a baseline " +
                        $"(min={minimum}, max={maximum}).");
                }

                counters[counter.Name] = maximum;
            }

            if (!scenarios.TryAdd(id, new PerfScenarioBaseline(counters)))
                throw new InvalidDataException($"summary contains duplicate scenario '{id}'.");
        }

        if (scenarios.Count == 0)
            throw new InvalidDataException("summary contains no performance scenarios.");

        var quality = Required(summary, "quality");
        var extraction = Required(summary, "extractionQuality");
        var tolerance = Required(Required(summary, "qualityGate"), "tolerance").GetDouble();

        return new PerfBaselineDocument(
            SchemaVersion: 1,
            Profile: $"{profile}-{scale}",
            QualityTolerance: tolerance,
            Scenarios: scenarios,
            Quality: new PerfQualityBaseline(
                RecallAtK: Required(quality, "recallAtK").GetDouble(),
                Mrr: Required(quality, "mrr").GetDouble(),
                CasesWithViolations: Required(quality, "casesWithViolations").GetInt32(),
                EntityPrecision: Required(extraction, "entityPrecision").GetDouble(),
                EntityRecall: Required(extraction, "entityRecall").GetDouble(),
                FactPrecision: Required(extraction, "factPrecision").GetDouble(),
                FactRecall: Required(extraction, "factRecall").GetDouble(),
                PreferencePrecision: Required(extraction, "preferencePrecision").GetDouble(),
                PreferenceRecall: Required(extraction, "preferenceRecall").GetDouble(),
                ExtractionFalsePositiveRate: Required(extraction, "falsePositiveRate").GetDouble()));
    }

    public static PerfBaselineDocument Load(string path)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException($"performance baseline not found: {path}", path);

        var baseline = JsonSerializer.Deserialize<PerfBaselineDocument>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"performance baseline is empty: {path}");
        if (baseline.SchemaVersion != 1)
        {
            throw new InvalidDataException(
                $"unsupported performance baseline schema {baseline.SchemaVersion}; expected 1.");
        }

        return baseline;
    }

    public void Save(string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        File.WriteAllText(path, JsonSerializer.Serialize(this, Json) + Environment.NewLine);
    }

    private static JsonElement Required(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value))
            throw new InvalidDataException($"performance summary is missing '{property}'.");
        return value;
    }
}

internal sealed record PerfScenarioBaseline(IReadOnlyDictionary<string, long> Counters);

internal sealed record PerfQualityBaseline(
    double RecallAtK,
    double Mrr,
    int CasesWithViolations,
    double EntityPrecision,
    double EntityRecall,
    double FactPrecision,
    double FactRecall,
    double PreferencePrecision,
    double PreferenceRecall,
    double ExtractionFalsePositiveRate);

internal sealed record PerfCounterChangeOverride(bool Allowed, string? PullRequestBody)
{
    private static readonly Regex JustificationPattern = new(
        @"^\s*Perf counter change justification:\s*(?<reason>\S.*?)\s*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase |
        RegexOptions.Multiline);

    public string? Justification =>
        PullRequestBody is null
            ? null
            : JustificationPattern.Match(PullRequestBody) is { Success: true } match
                ? match.Groups["reason"].Value
                : null;
}

internal sealed record PerfGateResult(
    bool Passed,
    IReadOnlyList<string> Violations,
    IReadOnlyList<string> AcknowledgedCounterChanges);

internal static class PerfRegressionGate
{
    private const double BytesIncreaseLimit = 0.05;

    public static PerfGateResult Evaluate(
        PerfBaselineDocument baseline,
        JsonElement report,
        PerfCounterChangeOverride counterOverride)
    {
        var actual = PerfBaselineDocument.FromSummary(report);
        var violations = new List<string>();
        var acknowledged = new List<string>();

        foreach (var (scenarioId, expectedScenario) in baseline.Scenarios)
        {
            if (!actual.Scenarios.TryGetValue(scenarioId, out var actualScenario))
            {
                violations.Add($"missing required scenario {scenarioId}.");
                continue;
            }

            var counterNames = expectedScenario.Counters.Keys
                .Concat(actualScenario.Counters.Keys)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal);
            foreach (var counter in counterNames)
            {
                var expected = expectedScenario.Counters.GetValueOrDefault(counter);
                var observed = actualScenario.Counters.GetValueOrDefault(counter);
                if (counter is "bytes_est" or "neo4j.bytes_est")
                {
                    if (BytesIncreasedTooFar(expected, observed))
                    {
                        violations.Add(
                            $"{scenarioId} bytes_est increased more than 5%: {expected} -> {observed}.");
                    }
                    continue;
                }

                if (!IsGuardedCounter(counter) || observed <= expected)
                    continue;

                var message = $"{scenarioId} {counter} increased: {expected} -> {observed}.";
                if (counterOverride.Allowed && counterOverride.Justification is not null)
                    acknowledged.Add($"{message} {counterOverride.Justification}");
                else
                    violations.Add(message);
            }
        }

        if (counterOverride.Allowed &&
            counterOverride.Justification is null &&
            HasCounterIncrease(baseline, actual))
        {
            violations.Add(
                "perf-counter-change requires a non-empty 'Perf counter change justification:' line.");
        }

        CompareQuality(baseline, actual, violations);
        return new PerfGateResult(violations.Count == 0, violations, acknowledged);
    }

    private static bool HasCounterIncrease(
        PerfBaselineDocument baseline,
        PerfBaselineDocument actual) =>
        baseline.Scenarios.Any(pair =>
            actual.Scenarios.TryGetValue(pair.Key, out var actualScenario) &&
            pair.Value.Counters.Keys
                .Concat(actualScenario.Counters.Keys)
                .Distinct(StringComparer.Ordinal)
                .Any(counter =>
                    IsGuardedCounter(counter) &&
                    actualScenario.Counters.GetValueOrDefault(counter) >
                    pair.Value.Counters.GetValueOrDefault(counter)));

    private static bool IsGuardedCounter(string counter) =>
        counter.StartsWith("neo4j.tx", StringComparison.Ordinal) ||
        counter is "neo4j.queries" or "embed.requests" or "llm.calls";

    private static bool BytesIncreasedTooFar(long expected, long actual) =>
        expected == 0
            ? actual > 0
            : actual > expected * (1 + BytesIncreaseLimit);

    private static void CompareQuality(
        PerfBaselineDocument baseline,
        PerfBaselineDocument actual,
        ICollection<string> violations)
    {
        var expected = baseline.Quality;
        var observed = actual.Quality;
        var tolerance = baseline.QualityTolerance;

        Minimum("recallAtK", expected.RecallAtK, observed.RecallAtK, tolerance, violations);
        Minimum("mrr", expected.Mrr, observed.Mrr, tolerance, violations);
        Maximum(
            "casesWithViolations",
            expected.CasesWithViolations,
            observed.CasesWithViolations,
            tolerance,
            violations);
        Minimum(
            "entityPrecision", expected.EntityPrecision, observed.EntityPrecision, tolerance, violations);
        Minimum("entityRecall", expected.EntityRecall, observed.EntityRecall, tolerance, violations);
        Minimum("factPrecision", expected.FactPrecision, observed.FactPrecision, tolerance, violations);
        Minimum("factRecall", expected.FactRecall, observed.FactRecall, tolerance, violations);
        Minimum(
            "preferencePrecision",
            expected.PreferencePrecision,
            observed.PreferencePrecision,
            tolerance,
            violations);
        Minimum(
            "preferenceRecall",
            expected.PreferenceRecall,
            observed.PreferenceRecall,
            tolerance,
            violations);
        Maximum(
            "extractionFalsePositiveRate",
            expected.ExtractionFalsePositiveRate,
            observed.ExtractionFalsePositiveRate,
            tolerance,
            violations);
    }

    private static void Minimum(
        string metric,
        double expected,
        double actual,
        double tolerance,
        ICollection<string> violations)
    {
        if (actual < expected - tolerance)
            violations.Add($"{metric} decreased: {expected:F6} -> {actual:F6}.");
    }

    private static void Maximum(
        string metric,
        double expected,
        double actual,
        double tolerance,
        ICollection<string> violations)
    {
        if (actual > expected + tolerance)
            violations.Add($"{metric} increased: {expected:F6} -> {actual:F6}.");
    }
}
