using System.Diagnostics;

namespace AgentMemory.Abstractions.Diagnostics;

/// <summary>
/// The single <see cref="ActivitySource"/> shared by every AgentMemory assembly.
/// </summary>
/// <remarks>
/// <para>
/// This lives in Abstractions — the one package every other package already references — so the Core,
/// Neo4j, and adapter assemblies can emit spans without taking a dependency on the opt-in
/// <c>AgentMemory.Observability</c> package (which would invert the layering established by ADR 0011).
/// <c>MemoryActivitySource.Instance</c> in Observability forwards to this same instance, so there is
/// exactly one source named <see cref="SourceName"/> in the process and a single listener sees
/// everything.
/// </para>
/// <para>
/// <b>Cost when nobody is listening.</b> <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <c>null</c> unless a listener is attached, so an instrumented call site costs one null check.
/// Any tag whose <em>value</em> is expensive to compute must still be guarded explicitly — write
/// <c>if (activity is not null) activity.SetTag(...)</c> rather than <c>activity?.SetTag(Expensive())</c>,
/// because the latter evaluates the argument before the null-conditional short-circuits.
/// </para>
/// </remarks>
public static class AgentMemoryDiagnostics
{
    /// <summary>Name to register with OpenTelemetry (or a raw <see cref="ActivityListener"/>).</summary>
    public const string SourceName = "AgentMemory";

    /// <summary>The shared source. Never null; never disposed for the lifetime of the process.</summary>
    public static readonly ActivitySource Source = new(SourceName, "1.0.0");

    /// <summary>
    /// True when at least one listener is attached. Use to guard work that exists <em>only</em> to
    /// produce telemetry and that is not already covered by a null <see cref="Activity"/>.
    /// </summary>
    public static bool IsEnabled => Source.HasListeners();
}
