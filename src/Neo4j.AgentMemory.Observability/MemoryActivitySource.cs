using System.Diagnostics;

namespace Neo4j.AgentMemory.Observability;

/// <summary>
/// Centralized <see cref="ActivitySource"/> for distributed tracing across all memory operations.
/// </summary>
public static class MemoryActivitySource
{
    /// <summary>
    /// The name used when registering the activity source with OpenTelemetry.
    /// </summary>
    public const string Name = "Neo4j.AgentMemory";

    /// <summary>
    /// Shared instance for all memory operation spans.
    /// </summary>
    public static readonly ActivitySource Instance = new(Name, "1.0.0");
}
