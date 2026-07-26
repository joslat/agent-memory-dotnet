using System.Globalization;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// One side of a hermetic A/B comparison. Specs intentionally cover options that can be varied inside
/// one process; code-only changes still require two builds plus a ledger diff.
/// </summary>
internal sealed record PerfConfiguration(string CanonicalSpec, RecallOptions Recall)
{
    public static PerfConfiguration Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return new PerfConfiguration("default", RecallOptions.Default);
        }

        var recall = RecallOptions.Default;
        var canonical = new List<string>();
        var assignments = value.Split(
            [',', ';'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (assignments.Length == 0)
            throw new ArgumentException("configuration spec is empty. Use 'default' or key=value.");

        foreach (var assignment in assignments)
        {
            var equals = assignment.IndexOf('=');
            if (equals <= 0 || equals == assignment.Length - 1)
                throw new ArgumentException(
                    $"invalid configuration assignment '{assignment}'. Use key=value.");

            var suppliedKey = assignment[..equals].Trim();
            var normalizedKey = Normalize(suppliedKey);
            var suppliedValue = assignment[(equals + 1)..].Trim();

            switch (normalizedKey)
            {
                case "recallmaxrecentmessages":
                    var maxRecent = ParseNonNegativeInt(suppliedValue, suppliedKey);
                    recall = recall with { MaxRecentMessages = maxRecent };
                    canonical.Add($"Recall.MaxRecentMessages={maxRecent}");
                    break;
                case "recallmaxrelevantmessages":
                    var maxRelevant = ParseNonNegativeInt(suppliedValue, suppliedKey);
                    recall = recall with { MaxRelevantMessages = maxRelevant };
                    canonical.Add($"Recall.MaxRelevantMessages={maxRelevant}");
                    break;
                case "recallmaxentities":
                    var maxEntities = ParseNonNegativeInt(suppliedValue, suppliedKey);
                    recall = recall with { MaxEntities = maxEntities };
                    canonical.Add($"Recall.MaxEntities={maxEntities}");
                    break;
                case "recallmaxfacts":
                    var maxFacts = ParseNonNegativeInt(suppliedValue, suppliedKey);
                    recall = recall with { MaxFacts = maxFacts };
                    canonical.Add($"Recall.MaxFacts={maxFacts}");
                    break;
                case "recallmaxpreferences":
                    var maxPreferences = ParseNonNegativeInt(suppliedValue, suppliedKey);
                    recall = recall with { MaxPreferences = maxPreferences };
                    canonical.Add($"Recall.MaxPreferences={maxPreferences}");
                    break;
                case "recallmaxtraces":
                    var maxTraces = ParseNonNegativeInt(suppliedValue, suppliedKey);
                    recall = recall with { MaxTraces = maxTraces };
                    canonical.Add($"Recall.MaxTraces={maxTraces}");
                    break;
                case "recallmaxgraphragitems":
                    var maxGraphRag = ParseNonNegativeInt(suppliedValue, suppliedKey);
                    recall = recall with { MaxGraphRagItems = maxGraphRag };
                    canonical.Add($"Recall.MaxGraphRagItems={maxGraphRag}");
                    break;
                case "recallminsimilarityscore":
                    var minimum = ParseUnitDouble(suppliedValue, suppliedKey);
                    recall = recall with { MinSimilarityScore = minimum };
                    canonical.Add(
                        $"Recall.MinSimilarityScore={minimum.ToString("G17", CultureInfo.InvariantCulture)}");
                    break;
                default:
                    throw new ArgumentException(
                        $"unknown configuration key '{suppliedKey}'. Supported keys are Recall.MaxRecentMessages, " +
                        "Recall.MaxRelevantMessages, Recall.MaxEntities, Recall.MaxFacts, " +
                        "Recall.MaxPreferences, Recall.MaxTraces, Recall.MaxGraphRagItems, and " +
                        "Recall.MinSimilarityScore.");
            }
        }

        return new PerfConfiguration(string.Join(';', canonical), recall);
    }

    private static string Normalize(string key) =>
        new(key.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static int ParseNonNegativeInt(string value, string key)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0)
        {
            throw new ArgumentException($"{key} must be a non-negative integer.");
        }

        return parsed;
    }

    private static double ParseUnitDouble(string value, string key)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            !double.IsFinite(parsed) ||
            parsed is < 0 or > 1)
        {
            throw new ArgumentException($"{key} must be a number between 0 and 1.");
        }

        return parsed;
    }
}
