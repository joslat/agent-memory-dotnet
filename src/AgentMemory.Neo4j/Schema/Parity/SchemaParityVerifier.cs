namespace AgentMemory.Neo4j.Schema.Parity;

/// <summary>
/// Compatibility-kit (TCK) verifier: compares a <see cref="SchemaDescriptor"/> of the .NET schema against
/// a captured upstream snapshot under a <see cref="SchemaParityPolicy"/> and produces a
/// <see cref="SchemaParityReport"/>. Reusable across the unit/regression test and the
/// <c>agentmemory schema-parity</c> CLI self-verification, so both judge compatibility identically.
/// </summary>
public static class SchemaParityVerifier
{
    /// <summary>
    /// Verifies the live .NET schema against the embedded upstream snapshot for
    /// <paramref name="upstreamVersion"/> using that version's documented divergence policy.
    /// </summary>
    public static SchemaParityReport VerifyDotNet(string upstreamVersion, UpstreamSchemaRegistry? registry = null)
    {
        registry ??= new UpstreamSchemaRegistry();
        var upstream = registry.Load(upstreamVersion);
        var policy = SchemaParityPolicy.ForVersion(upstreamVersion);
        return Verify(DotNetSchema.Describe(), upstream, policy);
    }

    /// <summary>Compares two descriptors under a divergence policy.</summary>
    public static SchemaParityReport Verify(SchemaDescriptor net, SchemaDescriptor upstream, SchemaParityPolicy policy)
    {
        // Breaks ----------------------------------------------------------------
        // Upstream labels/rels the .NET port should mirror but doesn't (minus the documented omissions).
        var missingLabels = upstream.NodeLabels
            .Except(net.NodeLabels)
            .Except(policy.UpstreamOnlyLabels)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        var missingRels = upstream.RelationshipTypes
            .Except(net.RelationshipTypes)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // .NET-only labels/rels that aren't on the documented extension allowlist.
        var undocumentedNetLabels = net.NodeLabels
            .Except(upstream.NodeLabels)
            .Except(policy.NetOnlyLabels)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        var undocumentedNetRels = net.RelationshipTypes
            .Except(upstream.RelationshipTypes)
            .Except(policy.NetOnlyRelationshipTypes)
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // Interop-critical property names that .NET fails to spell exactly as upstream.
        var interopDrift = policy.InteropCriticalProperties
            .Where(p => !net.Properties.Contains(p) || !upstream.Properties.Contains(p))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // .NET supersets that this upstream version now also has — the divergence note is stale, revisit it.
        var caughtUp = policy.NetSupersetProperties
            .Where(p => upstream.Properties.Contains(p))
            .OrderBy(x => x, StringComparer.Ordinal).ToList();

        // Documented, intentional divergences that are actually present (informational) ----------------
        var documented = new List<string>();
        foreach (var x in policy.UpstreamOnlyLabels.Intersect(upstream.NodeLabels).OrderBy(x => x, StringComparer.Ordinal))
            documented.Add($"upstream-only label not implemented in .NET: {x}");
        foreach (var x in policy.NetOnlyRelationshipTypes.Intersect(net.RelationshipTypes).OrderBy(x => x, StringComparer.Ordinal))
            documented.Add($".NET-only relationship extension: {x}");
        foreach (var x in policy.NetSupersetProperties.Intersect(net.Properties).Except(upstream.Properties).OrderBy(x => x, StringComparer.Ordinal))
            documented.Add($".NET property superset (absent upstream): {x}");

        return new SchemaParityReport(
            UpstreamVersion: policy.UpstreamVersion,
            MissingLabels: missingLabels,
            MissingRelationshipTypes: missingRels,
            UndocumentedNetOnlyLabels: undocumentedNetLabels,
            UndocumentedNetOnlyRelationshipTypes: undocumentedNetRels,
            InteropPropertyDrift: interopDrift,
            SupersetsUpstreamCaughtUpTo: caughtUp,
            DocumentedDivergences: documented);
    }
}
