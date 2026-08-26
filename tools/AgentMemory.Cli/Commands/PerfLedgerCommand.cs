using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Cli.Perf;

namespace AgentMemory.Cli.Commands;

/// <summary>Appends one summary-derived entry to the curated performance ledger.</summary>
public sealed class PerfLedgerCommand(TextWriter output)
{
    public const string DefaultLedgerPath = "strategy/performance/ledger.json";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private static readonly HashSet<string> Verdicts = new(StringComparer.Ordinal)
    {
        "improvement",
        "no-effect",
        "reverted",
    };

    public async Task<int> ExecuteAsync(
        string? runDirectory,
        string? comparedToValue,
        string? verdictValue,
        string? ledgerPathValue,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(runDirectory))
        {
            output.WriteLine("error: perf ledger add requires --run <directory>.");
            return 1;
        }

        var runPath = Path.GetFullPath(runDirectory);
        var summaryPath = Path.Combine(runPath, "summary.json");
        if (!File.Exists(summaryPath))
        {
            output.WriteLine($"error: performance summary not found: {summaryPath}");
            return 1;
        }

        if (!int.TryParse(
                comparedToValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var comparedTo) ||
            comparedTo < 0)
        {
            output.WriteLine("error: perf ledger add requires non-negative --compared-to <seq>.");
            return 1;
        }

        var verdict = verdictValue?.Trim().ToLowerInvariant();
        if (verdict is null || !Verdicts.Contains(verdict))
        {
            output.WriteLine(
                "error: --verdict must be improvement, no-effect, or reverted.");
            return 1;
        }

        var ledgerPath = Path.GetFullPath(ledgerPathValue ?? DefaultLedgerPath);
        if (!File.Exists(ledgerPath))
        {
            output.WriteLine($"error: performance ledger not found: {ledgerPath}");
            return 1;
        }

        var summaryText = await File.ReadAllTextAsync(
            summaryPath, cancellationToken).ConfigureAwait(false);
        using var summaryDocument = JsonDocument.Parse(summaryText);
        var summary = summaryDocument.RootElement;
        var baseline = PerfBaselineDocument.FromSummary(summary);
        var fingerprint = PerfLedgerFingerprint.FromSummary(summary, baseline);
        var sourceHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(summaryText)));

        var ledgerDirectory = Path.GetDirectoryName(ledgerPath)
            ?? throw new InvalidDataException("Ledger path has no parent directory.");
        Directory.CreateDirectory(ledgerDirectory);
        var lockPath = ledgerPath + ".lock";
        await using var ledgerLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.DeleteOnClose);

        var ledgerText = await File.ReadAllTextAsync(
            ledgerPath, cancellationToken).ConfigureAwait(false);
        var ledger = JsonNode.Parse(ledgerText)?.AsObject()
            ?? throw new InvalidDataException("Performance ledger is empty.");
        if (ledger["schemaVersion"]?.GetValue<int>() != 1)
            throw new InvalidDataException("Performance ledger schemaVersion must be 1.");

        var entries = ledger["entries"]?.AsArray()
            ?? throw new InvalidDataException("Performance ledger is missing entries.");
        ValidateContiguousSequence(entries);
        if (comparedTo >= entries.Count)
        {
            throw new InvalidDataException(
                $"Compared-to seq {comparedTo} does not exist; ledger ends at {entries.Count - 1}.");
        }

        var target = entries[comparedTo]?.AsObject()
            ?? throw new InvalidDataException($"Ledger seq {comparedTo} is not an object.");
        var targetFingerprintNode = target["fingerprint"]
            ?? throw new InvalidDataException(
                $"Ledger seq {comparedTo} has no fingerprint and cannot be compared safely.");
        var targetFingerprint = targetFingerprintNode.Deserialize<PerfLedgerFingerprint>(Json)
            ?? throw new InvalidDataException($"Ledger seq {comparedTo} has an invalid fingerprint.");
        ValidateComparable(fingerprint, targetFingerprint, target, comparedTo);

        foreach (var existing in entries.OfType<JsonObject>())
        {
            if (string.Equals(
                    existing["sourceSummarySha256"]?.GetValue<string>(),
                    sourceHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"A ledger entry for summary SHA-256 {sourceHash} already exists.");
            }
        }

        var nextSequence = entries.Count;
        var entry = BuildEntry(
            nextSequence,
            comparedTo,
            verdict,
            runPath,
            sourceHash,
            summary,
            baseline,
            fingerprint);
        entries.Add(entry);

        var temporaryPath = ledgerPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                ledger.ToJsonString(Json) + Environment.NewLine,
                cancellationToken).ConfigureAwait(false);
            File.Move(temporaryPath, ledgerPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }

        output.WriteLine(
            $"perf ledger: appended seq {nextSequence} from {summaryPath}");
        return 0;
    }

    private static JsonObject BuildEntry(
        int sequence,
        int comparedTo,
        string verdict,
        string runPath,
        string sourceHash,
        JsonElement summary,
        PerfBaselineDocument baseline,
        PerfLedgerFingerprint fingerprint)
    {
        var manifest = Required(summary, "manifest");
        var label = Required(manifest, "label").GetString()
            ?? throw new InvalidDataException("Summary label is null.");
        var startedAt = Required(manifest, "startedAtUtc").GetString()
            ?? throw new InvalidDataException("Summary startedAtUtc is null.");
        var environment = Required(manifest, "environment");
        var commit = Required(environment, "commit").ValueKind == JsonValueKind.Null
            ? null
            : Required(environment, "commit").GetString();
        var runId = Required(manifest, "runId").GetString()
            ?? throw new InvalidDataException("Summary runId is null.");

        var counters = new JsonObject();
        foreach (var (scenario, scenarioBaseline) in baseline.Scenarios)
        {
            var values = new JsonObject();
            foreach (var (name, value) in scenarioBaseline.Counters)
                values[name] = value;
            counters[scenario] = values;
        }

        var quality = new JsonObject
        {
            ["recallAtK"] = baseline.Quality.RecallAtK,
            ["mrr"] = baseline.Quality.Mrr,
            ["casesWithViolations"] = baseline.Quality.CasesWithViolations,
            ["entityPrecision"] = baseline.Quality.EntityPrecision,
            ["entityRecall"] = baseline.Quality.EntityRecall,
            ["factPrecision"] = baseline.Quality.FactPrecision,
            ["factRecall"] = baseline.Quality.FactRecall,
            ["preferencePrecision"] = baseline.Quality.PreferencePrecision,
            ["preferenceRecall"] = baseline.Quality.PreferenceRecall,
            ["extractionFalsePositiveRate"] = baseline.Quality.ExtractionFalsePositiveRate,
        };

        return new JsonObject
        {
            ["seq"] = sequence,
            ["label"] = label,
            ["slug"] = label,
            ["date"] = startedAt,
            ["commit"] = commit,
            ["runId"] = runId,
            ["sourceRun"] = PortableRunPath(runPath),
            ["sourceSummarySha256"] = sourceHash,
            ["comparedTo"] = comparedTo,
            ["verdict"] = verdict,
            ["accepted"] = verdict == "improvement",
            ["fingerprint"] = JsonSerializer.SerializeToNode(fingerprint, Json),
            ["counters"] = counters,
            ["quality"] = quality,
        };
    }

    private static void ValidateContiguousSequence(JsonArray entries)
    {
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index]?.AsObject()
                ?? throw new InvalidDataException($"Ledger entry {index} is not an object.");
            var sequence = entry["seq"]?.GetValue<int>()
                ?? throw new InvalidDataException($"Ledger entry {index} has no seq.");
            if (sequence != index)
            {
                throw new InvalidDataException(
                    $"Ledger sequence is not contiguous at index {index}: found seq {sequence}.");
            }
        }
    }

    private static void ValidateComparable(
        PerfLedgerFingerprint candidate,
        PerfLedgerFingerprint target,
        JsonObject targetEntry,
        int targetSequence)
    {
        Equal("profile", target.Profile, candidate.Profile);
        Equal("scale", target.Scale, candidate.Scale);
        Equal(
            "embedding dimensions",
            target.EmbeddingDimensions,
            candidate.EmbeddingDimensions);
        Equal(
            "embedding latency",
            target.EmbeddingLatencyMs,
            candidate.EmbeddingLatencyMs);
        Equal("model latency", target.ModelLatencyMs, candidate.ModelLatencyMs);
        Equal("Neo4j image", target.Neo4jImage, candidate.Neo4jImage);

        var targetScenarios = target.Scenarios.ToHashSet(StringComparer.Ordinal);
        var candidateScenarios = candidate.Scenarios.ToHashSet(StringComparer.Ordinal);
        var targetCounters = targetEntry["counters"]?.AsObject()
            ?? throw new InvalidDataException(
                $"Ledger seq {targetSequence} has no scenario counters.");

        foreach (var scenario in candidateScenarios)
        {
            if (!targetScenarios.Contains(scenario) || !targetCounters.ContainsKey(scenario))
            {
                throw new InvalidDataException(
                    $"Scenario '{scenario}' is absent from compared-to seq {targetSequence}.");
            }
        }

        // The other direction, which went unchecked. Only the candidate-subset-of-target half was
        // asserted, so a run that SILENTLY STOPPED EMITTING a scenario compared clean on the ones it
        // still had -- passable by absence, and hiding in the direction that flatters the run. The
        // ledger exists to compare like with like over time; a comparison quietly covering fewer
        // scenarios than its baseline is not that comparison, and nothing in the output said so.
        var dropped = targetScenarios.Except(candidateScenarios, StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        if (dropped.Length > 0)
        {
            throw new InvalidDataException(
                $"Run is missing {dropped.Length} scenario(s) present in compared-to seq "
                + $"{targetSequence}: {string.Join(", ", dropped)}. A ledger comparison must cover "
                + "the same scenario set on both sides.");
        }
    }

    private static void Equal<T>(string field, T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidDataException(
                $"Incomparable {field}: compared-to={expected}, run={actual}.");
        }
    }

    private static string PortableRunPath(string runPath)
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, runPath);
        return relative.Replace('\\', '/');
    }

    private static JsonElement Required(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value))
            throw new InvalidDataException($"Performance summary is missing '{property}'.");
        return value;
    }
}

internal sealed record PerfLedgerFingerprint(
    string Profile,
    string Scale,
    int EmbeddingDimensions,
    double EmbeddingLatencyMs,
    double ModelLatencyMs,
    string Neo4jImage,
    IReadOnlyList<string> Scenarios)
{
    public static PerfLedgerFingerprint FromSummary(
        JsonElement summary,
        PerfBaselineDocument baseline)
    {
        var manifest = Required(summary, "manifest");
        var environment = Required(manifest, "environment");
        return new PerfLedgerFingerprint(
            Required(manifest, "profile").GetString()
                ?? throw new InvalidDataException("Summary profile is null."),
            Required(manifest, "scale").GetString()
                ?? throw new InvalidDataException("Summary scale is null."),
            Required(environment, "embeddingDimensions").GetInt32(),
            Required(environment, "embeddingLatencyMs").GetDouble(),
            Required(environment, "modelLatencyMs").GetDouble(),
            Required(environment, "neo4jImage").GetString()
                ?? throw new InvalidDataException("Summary Neo4j image is null."),
            baseline.Scenarios.Keys.OrderBy(value => value, StringComparer.Ordinal).ToArray());
    }

    private static JsonElement Required(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var value))
            throw new InvalidDataException($"Performance summary is missing '{property}'.");
        return value;
    }
}
