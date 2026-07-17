namespace AgentMemory.Nams;

/// <summary>
/// Identifies the NAMS backend for logging/telemetry/diagnostics (e.g. a <c>backend = nams</c> tag,
/// matching the direct-Neo4j backend's own implicit identity). Deliberately minimal and NAMS-only: it does
/// NOT anticipate the engineering plan's later backend-neutral <c>MemoryBackendDescriptor</c>/
/// <c>MemoryBackendCapabilities</c> shape (a shared abstraction spanning both backends, proposed only for
/// the plan's optional convergence phase, well after NAMS behavior has actually been observed). Designing
/// that shared shape now, before there is a second real backend to generalize from, is exactly what the
/// plan's own ADR-4 warns against one level up (don't refactor toward a shared abstraction before the
/// concrete thing being generalized has been built and proven).
/// </summary>
public sealed class NamsBackendDescriptor
{
    /// <summary>The single shared instance -- this descriptor carries no per-registration state.</summary>
    public static readonly NamsBackendDescriptor Instance = new();

    /// <summary>Stable, lowercase backend identifier suitable for low-cardinality telemetry tags.</summary>
    public string Name => "nams";

    /// <summary>Human-readable name for diagnostics/UI surfaces.</summary>
    public string DisplayName => "Neo4j Agent Memory as a Service (NAMS)";

    private NamsBackendDescriptor()
    {
    }
}
