using System.Globalization;
using System.Text.Json;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Recomputes the counting census over reports that already exist. Spends nothing.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every TypedMemEval report retains <c>GoldAnswer</c> and <c>AgentResponse</c> per question</b>,
/// so the statistic the identity preregs decide on can be derived from runs that were already paid
/// for. It never was: the figures those preregs rest on — "1 exact, 14 undercounts, 0 overcounts,
/// 15 of 43 gold recovered" — were read off by hand and live only in correspondence.
/// </para>
/// <para>
/// That matters beyond tidiness. A hand-derived statistic cannot be re-derived when someone doubts
/// it, cannot be red-probed, and its Goodhart guard is a promise rather than a check. This verb
/// turns roughly thirty already-bought artifacts into evidence that can be re-examined at any time
/// for nothing — which is the right direction of travel when the measurement budget is spent.
/// </para>
/// </remarks>
internal static class TypedMemEvalCensusProgram
{
    internal static Task<int> RunAsync(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var paths = args
            .SkipWhile(arg => !string.Equals(arg, "--census", StringComparison.Ordinal))
            .Skip(1)
            .TakeWhile(arg => !arg.StartsWith("--", StringComparison.Ordinal))
            .SelectMany(Expand)
            .Where(path => !path.EndsWith(".provenance.json", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        if (paths.Length == 0)
        {
            Console.Error.WriteLine(
                "census: usage --census <report.json|directory> [...]. Reads reports already on disk; "
                + "spends nothing.");
            return Task.FromResult(2);
        }

        foreach (var path in paths) Report(path);
        return Task.FromResult(0);
    }

    private static IEnumerable<string> Expand(string path) =>
        Directory.Exists(path)
            ? Directory.EnumerateFiles(path, "typedmemeval-*.json")
            : File.Exists(path) ? [path] : [];

    private static void Report(string path)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllBytes(path));
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            // An unreadable artifact is skipped and SAID to be skipped. Silently omitting it would
            // let a census report a population it never read.
            Console.Error.WriteLine($"census: {Path.GetFileName(path)} — unreadable, skipped");
            return;
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("QuestionResults", out var results)
                || results.ValueKind != JsonValueKind.Array)
            {
                Console.Error.WriteLine($"census: {Path.GetFileName(path)} — no QuestionResults");
                return;
            }

            var byShape = results.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.Object)
                .GroupBy(item => Str(item, "QuestionType") ?? "(none)", StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal);

            Console.WriteLine($"census: {Path.GetFileName(path)}");

            foreach (var shape in byShape)
            {
                var census = TypedMemEvalCountCensus.Measure(
                    shape.Select(item => (Str(item, "GoldAnswer"), Str(item, "AgentResponse"))));

                // A shape with nothing countable is reported as such rather than as a row of zeros:
                // "no counting questions here" and "every count was wrong" are opposite facts.
                if (census.Scored == 0)
                {
                    Console.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"census:   {shape.Key} — no counting questions ({census.NotCountable} excluded)"));
                    continue;
                }

                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"census:   {shape.Key} — {census.Describe()}"));

                if (!census.OverMergeGuardClear)
                {
                    Console.Error.WriteLine(
                        $"census:   ⛔ OVER-MERGE GUARD — {shape.Key} has {census.Overcounts} "
                        + "overcount(s). Counts above gold mean evidence arrived that should not "
                        + "have; a run in this state does not confirm, whatever its score does.");
                }
            }
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
