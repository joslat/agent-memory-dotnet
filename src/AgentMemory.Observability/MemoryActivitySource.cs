using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;

namespace AgentMemory.Observability;

/// <summary>
/// Centralized <see cref="ActivitySource"/> for distributed tracing across all memory operations.
/// </summary>
/// <remarks>
/// The instance itself now lives in <see cref="AgentMemoryDiagnostics"/> (in Abstractions) so the Core,
/// Neo4j, and adapter assemblies can emit spans without referencing this opt-in package. This type is
/// retained as the public, documented entry point and forwards to that single shared instance — the name
/// and the object identity are unchanged, so existing OpenTelemetry registrations keep working exactly
/// as before.
/// </remarks>
public static class MemoryActivitySource
{
    /// <summary>
    /// The name used when registering the activity source with OpenTelemetry.
    /// </summary>
    public const string Name = AgentMemoryDiagnostics.SourceName;

    /// <summary>
    /// Shared instance for all memory operation spans.
    /// </summary>
    public static readonly ActivitySource Instance = AgentMemoryDiagnostics.Source;
}
