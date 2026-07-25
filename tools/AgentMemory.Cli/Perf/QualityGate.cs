using System.Globalization;
using System.Text.Json;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Reviewable quality thresholds committed with the harness.
/// </summary>
internal sealed record QualityBaseline(
    int SchemaVersion,
    double Tolerance,
    RetrievalQualityBaseline Retrieval,
    ExtractionQualityBaseline Extraction);

internal sealed record RetrievalQualityBaseline(
    double RecallAtK,
    double Mrr,
    int Cases,
    int MaxCasesWithViolations);

internal sealed record ExtractionQualityBaseline(
    double EntityPrecision,
    double EntityRecall,
    double FactPrecision,
    double FactRecall,
    double PreferencePrecision,
    double PreferenceRecall,
    int Cases,
    int ExpectNothingCases,
    double MaxFalsePositiveRate);

/// <summary>The enforceable verdict written beside every quality report.</summary>
internal sealed record QualityGateResult(
    bool Enabled,
    bool Passed,
    string BaselinePath,
    double Tolerance,
    IReadOnlyList<string> Violations)
{
    public static QualityGateResult Disabled() =>
        new(false, true, QualityGate.DefaultBaselinePath, 0, []);
}

/// <summary>
/// Compares deterministic retrieval and extraction scores with the committed baseline.
/// </summary>
/// <remarks>
/// Lower-is-worse metrics use <c>actual + tolerance &lt; baseline</c>. False positives use the inverse
/// upper bound. Fixture case counts are exact: silently deleting a difficult judged case must not make
/// a run pass.
/// </remarks>
internal static class QualityGate
{
    internal const string DefaultBaselinePath = "eng/perf/baselines/quality.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static QualityBaseline LoadBaseline()
    {
        if (!File.Exists(DefaultBaselinePath))
        {
            throw new FileNotFoundException(
                $"Quality baseline not found at '{DefaultBaselinePath}'. Run perf from the repository root.",
                DefaultBaselinePath);
        }

        var baseline = JsonSerializer.Deserialize<QualityBaseline>(
            File.ReadAllText(DefaultBaselinePath), Json)
            ?? throw new InvalidOperationException(
                $"Quality baseline '{DefaultBaselinePath}' deserialized to null.");

        Validate(baseline);
        return baseline;
    }

    public static QualityGateResult Evaluate(
        QualityBaseline baseline,
        QualityResult retrieval,
        ExtractionQualityResult extraction,
        string baselinePath = DefaultBaselinePath)
    {
        Validate(baseline);
        var violations = new List<string>();

        RequireExact("retrieval.cases", retrieval.Cases, baseline.Retrieval.Cases, violations);
        RequireMinimum(
            "retrieval.recallAtK", retrieval.RecallAtK, baseline.Retrieval.RecallAtK,
            baseline.Tolerance, violations);
        RequireMinimum(
            "retrieval.mrr", retrieval.Mrr, baseline.Retrieval.Mrr,
            baseline.Tolerance, violations);

        if (retrieval.CasesWithViolations > baseline.Retrieval.MaxCasesWithViolations)
        {
            violations.Add(
                $"retrieval forbidden retrievals: actual {retrieval.CasesWithViolations}, " +
                $"allowed {baseline.Retrieval.MaxCasesWithViolations}");
        }

        RequireExact("extraction.cases", extraction.Cases, baseline.Extraction.Cases, violations);
        RequireExact(
            "extraction.expectNothingCases",
            extraction.ExpectNothingCases,
            baseline.Extraction.ExpectNothingCases,
            violations);
        RequireMinimum(
            "extraction.entityPrecision", extraction.EntityPrecision,
            baseline.Extraction.EntityPrecision, baseline.Tolerance, violations);
        RequireMinimum(
            "extraction.entityRecall", extraction.EntityRecall,
            baseline.Extraction.EntityRecall, baseline.Tolerance, violations);
        RequireMinimum(
            "extraction.factPrecision", extraction.FactPrecision,
            baseline.Extraction.FactPrecision, baseline.Tolerance, violations);
        RequireMinimum(
            "extraction.factRecall", extraction.FactRecall,
            baseline.Extraction.FactRecall, baseline.Tolerance, violations);
        RequireMinimum(
            "extraction.preferencePrecision", extraction.PreferencePrecision,
            baseline.Extraction.PreferencePrecision, baseline.Tolerance, violations);
        RequireMinimum(
            "extraction.preferenceRecall", extraction.PreferenceRecall,
            baseline.Extraction.PreferenceRecall, baseline.Tolerance, violations);

        if (extraction.FalsePositiveRate >
            baseline.Extraction.MaxFalsePositiveRate + baseline.Tolerance)
        {
            violations.Add(
                $"extraction.falsePositiveRate: actual {Format(extraction.FalsePositiveRate)}, " +
                $"maximum {Format(baseline.Extraction.MaxFalsePositiveRate)}, " +
                $"tolerance {Format(baseline.Tolerance)}");
        }

        return new QualityGateResult(
            Enabled: true,
            Passed: violations.Count == 0,
            BaselinePath: baselinePath.Replace('\\', '/'),
            Tolerance: baseline.Tolerance,
            Violations: violations);
    }

    private static void RequireMinimum(
        string metric,
        double actual,
        double expected,
        double tolerance,
        List<string> violations)
    {
        if (actual + tolerance < expected)
        {
            violations.Add(
                $"{metric}: actual {Format(actual)}, baseline {Format(expected)}, " +
                $"tolerance {Format(tolerance)}");
        }
    }

    private static void RequireExact(
        string metric,
        int actual,
        int expected,
        List<string> violations)
    {
        if (actual != expected)
            violations.Add($"{metric}: actual {actual}, baseline {expected} (must match exactly)");
    }

    private static string Format(double value) =>
        value.ToString("F6", CultureInfo.InvariantCulture);

    private static void Validate(QualityBaseline baseline)
    {
        if (baseline.SchemaVersion != 1)
            throw new InvalidOperationException(
                $"Unsupported quality baseline schemaVersion {baseline.SchemaVersion}; expected 1.");
        if (!double.IsFinite(baseline.Tolerance) || baseline.Tolerance < 0)
            throw new InvalidOperationException("Quality baseline tolerance must be finite and non-negative.");
        if (baseline.Retrieval.Cases <= 0 || baseline.Extraction.Cases <= 0 ||
            baseline.Extraction.ExpectNothingCases <= 0)
        {
            throw new InvalidOperationException("Quality baseline case counts must be positive.");
        }

        var scores = new[]
        {
            baseline.Retrieval.RecallAtK,
            baseline.Retrieval.Mrr,
            baseline.Extraction.EntityPrecision,
            baseline.Extraction.EntityRecall,
            baseline.Extraction.FactPrecision,
            baseline.Extraction.FactRecall,
            baseline.Extraction.PreferencePrecision,
            baseline.Extraction.PreferenceRecall,
            baseline.Extraction.MaxFalsePositiveRate,
        };
        if (scores.Any(score => !double.IsFinite(score) || score is < 0 or > 1))
            throw new InvalidOperationException("Quality baseline scores must be finite values from 0 through 1.");
        if (baseline.Retrieval.MaxCasesWithViolations < 0)
            throw new InvalidOperationException(
                "Quality baseline maxCasesWithViolations must be non-negative.");
    }
}
