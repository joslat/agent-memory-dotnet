namespace AgentMemory.Cli.Perf;

/// <summary>
/// Everything measured for one scenario iteration: exact structural counters, and per-span time.
/// </summary>
/// <remarks>
/// Counters are the durable, machine-independent part of a measurement — the same scenario on the same
/// data must produce the same counts on any machine, which is what makes them safe to assert on in CI.
/// Timings are recorded alongside but are only ever compared as ratios within a run.
/// Mutation is locked because recall fans out into concurrent tasks that all report into the same turn.
/// </remarks>
public sealed class TurnRecord
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _counters = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _spanMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _spanCount = new(StringComparer.Ordinal);

    public TurnRecord(string scenario, int iteration, string phase)
    {
        Scenario = scenario;
        Iteration = iteration;
        Phase = phase;
    }

    /// <summary>Scenario id, e.g. <c>PERF-R-04</c>.</summary>
    public string Scenario { get; }

    /// <summary>Zero-based iteration index within its phase.</summary>
    public int Iteration { get; }

    /// <summary><c>warmup</c> or <c>measure</c>. Warm-up rows are written but never aggregated.</summary>
    public string Phase { get; }

    /// <summary>Wall-clock duration of the whole iteration.</summary>
    public double DurationMs { get; internal set; }

    /// <summary>Adds to a structural counter.</summary>
    public void Add(string name, long value = 1)
    {
        lock (_gate)
        {
            _counters.TryGetValue(name, out var current);
            _counters[name] = current + value;
        }
    }

    /// <summary>Records one completed span: total time and occurrence count are both kept.</summary>
    public void RecordSpan(string name, double milliseconds)
    {
        lock (_gate)
        {
            _spanMs.TryGetValue(name, out var ms);
            _spanMs[name] = ms + milliseconds;
            _spanCount.TryGetValue(name, out var n);
            _spanCount[name] = n + 1;
        }
    }

    /// <summary>Reads a counter, or 0 when it never fired. Used by scenario self-assertions.</summary>
    public long Counter(string name)
    {
        lock (_gate) return _counters.TryGetValue(name, out var v) ? v : 0;
    }

    public IReadOnlyDictionary<string, long> Counters
    {
        get { lock (_gate) return new Dictionary<string, long>(_counters, StringComparer.Ordinal); }
    }

    public IReadOnlyDictionary<string, double> SpanMilliseconds
    {
        get { lock (_gate) return new Dictionary<string, double>(_spanMs, StringComparer.Ordinal); }
    }

    public IReadOnlyDictionary<string, long> SpanCounts
    {
        get { lock (_gate) return new Dictionary<string, long>(_spanCount, StringComparer.Ordinal); }
    }
}
