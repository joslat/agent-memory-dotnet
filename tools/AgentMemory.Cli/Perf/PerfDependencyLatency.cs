namespace AgentMemory.Cli.Perf;

/// <summary>
/// A named, deterministic dependency-delay shape applied by one performance scenario.
/// This is separate from the run-wide zero/remote provider profile so a degraded control can coexist
/// with ordinary scenarios without slowing or changing them.
/// </summary>
public sealed record PerfDependencyLatencyPreset(
    string Name,
    TimeSpan EmbeddingDelay,
    TimeSpan DatabaseDelay)
{
    /// <summary>
    /// PERF-R-07: a clearly degraded embedding provider plus a slower database transaction boundary.
    /// Long enough to dominate normal hermetic noise while keeping the two-profile CI run below five minutes.
    /// </summary>
    public static PerfDependencyLatencyPreset Degraded { get; } = new(
        "degraded",
        TimeSpan.FromSeconds(2),
        TimeSpan.FromMilliseconds(250));
}

/// <summary>
/// Carries a scenario's dependency-delay override through async recall fan-out without changing any
/// process-wide provider setting. Nested scopes restore the previous value on disposal.
/// </summary>
public sealed class PerfDependencyLatency
{
    private readonly AsyncLocal<PerfDependencyLatencyPreset?> _current = new();

    public PerfDependencyLatencyPreset? Current => _current.Value;

    public IDisposable Push(PerfDependencyLatencyPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        var previous = _current.Value;
        _current.Value = preset;
        return new RestoreScope(this, previous);
    }

    public TimeSpan ResolveEmbeddingDelay(TimeSpan runWideDelay) =>
        Current?.EmbeddingDelay ?? runWideDelay;

    private sealed class RestoreScope : IDisposable
    {
        private readonly PerfDependencyLatency _owner;
        private readonly PerfDependencyLatencyPreset? _previous;
        private bool _disposed;

        public RestoreScope(
            PerfDependencyLatency owner,
            PerfDependencyLatencyPreset? previous)
        {
            _owner = owner;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._current.Value = _previous;
        }
    }
}
