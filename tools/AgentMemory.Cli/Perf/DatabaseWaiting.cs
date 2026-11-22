using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// DRT-0 (C-00): what each scenario costs in database round trips, from a run with <c>--db-latency</c>. Two numbers,
/// because they differ. <c>roundTrips</c> is what the link proxy counted on the wire in the zero-latency iterations:
/// exact, whatever the driver does inside. <c>roundTripsWaited</c> is what a caller waits for, the difference of the
/// median durations with and without the delay divided by what one delayed round trip really cost (measured by the
/// self-check, a little above the delay set): round trips that overlap (reads started together) are waited for once.
/// </summary>
public sealed record DatabaseWaiting(
    [property: JsonPropertyName("roundTripMs")] double RoundTripMs,
    [property: JsonPropertyName("measuredRoundTripMs")] double MeasuredRoundTripMs,
    [property: JsonPropertyName("scenarios")] IReadOnlyList<DatabaseWaiting.ScenarioWaiting> Scenarios)
{
    public sealed record ScenarioWaiting(
        [property: JsonPropertyName("scenario")] string Scenario,
        [property: JsonPropertyName("roundTrips")] long RoundTrips,
        [property: JsonPropertyName("roundTripsMin")] long RoundTripsMin,
        [property: JsonPropertyName("roundTripsMax")] long RoundTripsMax,
        [property: JsonPropertyName("medianMs")] double MedianMs,
        [property: JsonPropertyName("medianMsWithDelay")] double? MedianMsWithDelay,
        [property: JsonPropertyName("roundTripsWaited")] double? RoundTripsWaited);

    /// <param name="roundTrip">The delay of the second set of iterations (zero: none ran, only the wire is counted).</param>
    /// <param name="measured">What one delayed round trip really cost, from the self-check.</param>
    /// <param name="near">The zero-latency measured iterations.</param>
    /// <param name="far">The iterations measured with the delay.</param>
    public static DatabaseWaiting From(TimeSpan roundTrip, TimeSpan measured, IReadOnlyList<TurnRecord> near, IReadOnlyList<TurnRecord> far)
    {
        var divisor = measured > TimeSpan.Zero ? measured : roundTrip;
        var scenarios = near
            .GroupBy(r => r.Scenario, StringComparer.Ordinal)
            .Select(g =>
            {
                var counts = g.Select(r => r.Counter("neo4j.round_trips")).ToList();
                var nearMs = Median(g.Select(r => r.DurationMs));
                var delayed = far.Where(r => r.Scenario == g.Key).Select(r => r.DurationMs).ToList();
                double? farMs = delayed.Count == 0 ? null : Median(delayed);
                double? waited = farMs is { } f && divisor > TimeSpan.Zero
                    ? Math.Round((f - nearMs) / divisor.TotalMilliseconds, 1)
                    : null;
                return new ScenarioWaiting(g.Key, (long)Median(counts.Select(c => (double)c)), counts.Min(), counts.Max(),
                    Math.Round(nearMs, 2), farMs is { } ms ? Math.Round(ms, 2) : null, waited);
            })
            .ToList();
        return new DatabaseWaiting(roundTrip.TotalMilliseconds, Math.Round(divisor.TotalMilliseconds, 2), scenarios);
    }

    /// <summary>The report's section: one row per scenario.</summary>
    public string Render()
    {
        var c = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.AppendLine();
        text.AppendLine(string.Create(c, $"## Database round trips (--db-latency {RoundTripMs:0} ms, measured {MeasuredRoundTripMs:0.00} ms)"));
        text.AppendLine();
        text.AppendLine("On the wire: counted by the link proxy in the zero-latency iterations. Waited: the median duration with");
        text.AppendLine("the delay minus the median without it, divided by the measured round trip; overlapping round trips count once.");
        text.AppendLine();
        text.AppendLine("| Scenario | On the wire | Waited | Median ms | Median ms with the delay |");
        text.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var s in Scenarios)
        {
            var wire = s.RoundTripsMin == s.RoundTripsMax
                ? s.RoundTrips.ToString(c)
                : string.Create(c, $"{s.RoundTrips} ({s.RoundTripsMin}-{s.RoundTripsMax})");
            text.AppendLine(string.Create(c,
                $"| {s.Scenario} | {wire} | {(s.RoundTripsWaited is { } w ? w.ToString("0.0", c) : "n/a")} | {s.MedianMs:0.00} | {(s.MedianMsWithDelay is { } f ? f.ToString("0.00", c) : "n/a")} |"));
        }
        return text.ToString();
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0) return 0;
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}
