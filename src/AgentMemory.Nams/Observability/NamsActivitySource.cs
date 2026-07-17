using System.Diagnostics;

namespace AgentMemory.Nams.Observability;

/// <summary>
/// Centralized <see cref="ActivitySource"/> for distributed tracing across NAMS backend operations
/// (engineering plan Phase 9). Uses the BCL diagnostics APIs directly rather than taking a dependency on
/// <c>AgentMemory.Observability</c> -- that package decorates the direct-Neo4j-backend's Core interfaces
/// (<c>IMemoryService</c>, extractors, etc.), none of which this package can reference (B9: zero sibling
/// <c>AgentMemory.*</c> references).
/// </summary>
public static class NamsActivitySource
{
    /// <summary>The name used when registering the activity source with OpenTelemetry.</summary>
    public const string Name = "AgentMemory.Nams";

    /// <summary>Shared instance for all NAMS backend operation spans.</summary>
    public static readonly ActivitySource Instance = new(Name, "1.0.0");
}
