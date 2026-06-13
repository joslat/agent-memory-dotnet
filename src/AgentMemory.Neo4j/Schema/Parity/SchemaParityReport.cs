using System.Text;

namespace AgentMemory.Neo4j.Schema.Parity;

/// <summary>
/// The outcome of a parity check between the .NET schema and a captured upstream snapshot under a
/// <see cref="SchemaParityPolicy"/>. <see cref="IsCompatible"/> is true only when every break list is
/// empty; the <see cref="DocumentedDivergences"/> list records the intentional deltas that were present
/// (informational, never a failure).
/// </summary>
public sealed record SchemaParityReport(
    string UpstreamVersion,
    IReadOnlyList<string> MissingLabels,
    IReadOnlyList<string> MissingRelationshipTypes,
    IReadOnlyList<string> MissingProperties,
    IReadOnlyList<string> UndocumentedNetOnlyLabels,
    IReadOnlyList<string> UndocumentedNetOnlyRelationshipTypes,
    IReadOnlyList<string> InteropPropertyDrift,
    IReadOnlyList<string> SupersetsUpstreamCaughtUpTo,
    IReadOnlyList<string> DocumentedDivergences)
{
    /// <summary>True when there are no compatibility breaks (only documented divergences, if any).</summary>
    public bool IsCompatible =>
        MissingLabels.Count == 0
        && MissingRelationshipTypes.Count == 0
        && MissingProperties.Count == 0
        && UndocumentedNetOnlyLabels.Count == 0
        && UndocumentedNetOnlyRelationshipTypes.Count == 0
        && InteropPropertyDrift.Count == 0
        && SupersetsUpstreamCaughtUpTo.Count == 0;

    /// <summary>All compatibility-break messages, flattened (empty when <see cref="IsCompatible"/>).</summary>
    public IReadOnlyList<string> Breaks
    {
        get
        {
            var b = new List<string>();
            foreach (var x in MissingLabels) b.Add($"missing upstream node label: {x}");
            foreach (var x in MissingRelationshipTypes) b.Add($"missing upstream relationship type: {x}");
            foreach (var x in MissingProperties) b.Add($"upstream property absent in .NET (renamed/dropped?): {x}");
            foreach (var x in UndocumentedNetOnlyLabels) b.Add($"undocumented .NET-only node label: {x}");
            foreach (var x in UndocumentedNetOnlyRelationshipTypes) b.Add($"undocumented .NET-only relationship type: {x}");
            foreach (var x in InteropPropertyDrift) b.Add($"interop property drift (missing/renamed in .NET): {x}");
            foreach (var x in SupersetsUpstreamCaughtUpTo) b.Add($"upstream caught up to .NET superset (revisit divergence): {x}");
            return b;
        }
    }

    /// <summary>A human-readable multi-line summary suitable for CLI output or a test failure message.</summary>
    public string Summary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Schema parity vs upstream neo4j-agent-memory v{UpstreamVersion}: {(IsCompatible ? "COMPATIBLE" : "INCOMPATIBLE")}");
        var breaks = Breaks;
        if (breaks.Count > 0)
        {
            sb.AppendLine($"  {breaks.Count} break(s):");
            foreach (var x in breaks) sb.AppendLine($"    ✗ {x}");
        }
        if (DocumentedDivergences.Count > 0)
        {
            sb.AppendLine($"  {DocumentedDivergences.Count} documented divergence(s) (intentional):");
            foreach (var x in DocumentedDivergences) sb.AppendLine($"    • {x}");
        }
        return sb.ToString().TrimEnd();
    }
}
