using System.Diagnostics.Metrics;

namespace AgentMemory.Nams.Observability;

/// <summary>
/// Centralized <see cref="Meter"/> with counters and histograms for NAMS backend operations (engineering
/// plan Phase 9). Every tag is a low-cardinality enum-shaped value (operation name, status, failure kind) --
/// never an owner/workspace/conversation/entity ID, memory text, or a prompt, matching the plan's own
/// "never tag" list.
/// </summary>
public sealed class NamsMetrics : IDisposable
{
    /// <summary>The meter name used when registering with OpenTelemetry.</summary>
    public const string MeterName = "AgentMemory.Nams";

    private readonly Meter _meter;

    public NamsMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        BackendOperations = _meter.CreateCounter<long>(
            "memory.backend.operations",
            description: "Number of NAMS backend operations, tagged by backend/operation/status");

        BackendDurationMs = _meter.CreateHistogram<double>(
            "memory.backend.duration",
            unit: "ms",
            description: "Duration of NAMS backend operations in milliseconds, tagged by backend/operation");

        BackendFailures = _meter.CreateCounter<long>(
            "memory.backend.failures",
            description: "Number of failed NAMS backend operations, tagged by backend/operation/failure_kind");

        BackendRetries = _meter.CreateCounter<long>(
            "memory.backend.retries",
            description: "Number of retry attempts against the NAMS backend, tagged by backend/operation");

        BackendRateLimited = _meter.CreateCounter<long>(
            "memory.backend.rate_limited",
            description: "Number of NAMS backend operations that ultimately failed as rate-limited, tagged by backend/operation");

        UnknownWriteOutcomes = _meter.CreateCounter<long>(
            "memory.backend.unknown_write_outcomes",
            description: "Number of persistence operations whose outcome was genuinely ambiguous (network/timeout failure with no confirmed NAMS idempotency to retry against)");

        ContextItems = _meter.CreateHistogram<long>(
            "memory.backend.context_items",
            description: "Number of items returned by a recall operation");

        ContextTruncated = _meter.CreateCounter<long>(
            "memory.backend.context_truncated",
            description: "Number of recall operations whose result was truncated or otherwise degraded/partial");
    }

    /// <summary>Disposes the underlying <see cref="Meter"/>.</summary>
    public void Dispose() => _meter.Dispose();

    /// <summary>Number of NAMS backend operations, tagged by backend/operation/status.</summary>
    internal Counter<long> BackendOperations { get; }

    /// <summary>Duration of NAMS backend operations in milliseconds, tagged by backend/operation.</summary>
    internal Histogram<double> BackendDurationMs { get; }

    /// <summary>Number of failed NAMS backend operations, tagged by backend/operation/failure_kind.</summary>
    internal Counter<long> BackendFailures { get; }

    /// <summary>Number of retry attempts against the NAMS backend, tagged by backend/operation.</summary>
    internal Counter<long> BackendRetries { get; }

    /// <summary>Number of NAMS backend operations that ultimately failed as rate-limited.</summary>
    internal Counter<long> BackendRateLimited { get; }

    /// <summary>Number of persistence operations with a genuinely ambiguous outcome.</summary>
    internal Counter<long> UnknownWriteOutcomes { get; }

    /// <summary>Number of items returned by a recall operation.</summary>
    internal Histogram<long> ContextItems { get; }

    /// <summary>Number of recall operations whose result was truncated or otherwise degraded/partial.</summary>
    internal Counter<long> ContextTruncated { get; }
}
