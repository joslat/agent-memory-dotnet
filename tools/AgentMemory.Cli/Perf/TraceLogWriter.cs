using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Cli.Perf;

/// <summary>
/// Append-only NDJSON trace log — the raw evidence for a run. Every other artifact is derived from it.
/// </summary>
/// <remarks>
/// <para>
/// One JSON object per line, flushed as each event happens, so a run that dies at iteration 47 of 50
/// still yields 46 usable iterations. Chosen over a single JSON document for exactly that reason, plus
/// it streams without being held in memory and it greps.
/// </para>
/// <para>
/// <b>Nothing here may contain memory content.</b> No message text, no entity names, no owner ids, no
/// query text, no Cypher (which can embed parameters). Spans contribute their operation name, duration,
/// and a small allow-list of low-cardinality tags. This rule exists so a trace log is always safe to
/// attach to a public issue — including one produced by a run against real data.
/// </para>
/// </remarks>
public sealed class TraceLogWriter : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Span tags allowed into the log. Anything not listed is dropped, not redacted.</summary>
    private static readonly string[] AllowedTags =
    [
        "db.mode",
        "db.query.fingerprint",
        "db.records",
        "db.bytes_est",
        "db.transaction_entry_ms_est",
        "memory.access_tracking.items",
        "memory.store.message_count",
        "memory.extract.source_messages",
    ];

    private readonly StreamWriter _writer;
    private readonly object _gate = new();

    public TraceLogWriter(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false) { AutoFlush = true };
    }

    public void RunStart(string runId, object manifest) =>
        Write(new { t = "run.start", runId, manifest });

    public void TurnStart(TurnRecord turn) =>
        Write(new { t = "turn.start", scenario = turn.Scenario, iteration = turn.Iteration, phase = turn.Phase });

    public void Span(TurnRecord turn, Activity activity)
    {
        Dictionary<string, object?>? tags = null;
        foreach (var key in AllowedTags)
        {
            var value = activity.GetTagItem(key);
            if (value is null) continue;
            (tags ??= new Dictionary<string, object?>(StringComparer.Ordinal))[key] = value;
        }

        Write(new
        {
            t = "span",
            scenario = turn.Scenario,
            iteration = turn.Iteration,
            name = activity.OperationName,
            // Microseconds as an integer: milliseconds lose resolution on fast local operations, and
            // floating point invites precision that the measurement does not have.
            durUs = (long)(activity.Duration.TotalMilliseconds * 1000),
            tags,
        });
    }

    public void TurnEnd(TurnRecord turn) =>
        Write(new
        {
            t = "turn.end",
            scenario = turn.Scenario,
            iteration = turn.Iteration,
            phase = turn.Phase,
            durUs = (long)(turn.DurationMs * 1000),
            counters = turn.Counters,
            samples = turn.Samples,
        });

    public void RunEnd(int turns, double durationMs) =>
        Write(new { t = "run.end", turns, durUs = (long)(durationMs * 1000) });

    private void Write(object payload)
    {
        var line = JsonSerializer.Serialize(payload, Json);
        lock (_gate) _writer.WriteLine(line);
    }

    public void Dispose() => _writer.Dispose();
}
