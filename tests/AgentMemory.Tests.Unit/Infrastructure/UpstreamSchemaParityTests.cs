using FluentAssertions;
using AgentMemory.Neo4j.Schema.Parity;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// Compatibility-kit (TCK) regression guard. Drives the reusable <see cref="SchemaParityVerifier"/> — the
/// same engine the <c>agentmemory schema-parity</c> CLI uses — against the embedded upstream
/// <c>neo4j-agent-memory v0.5.0</c> snapshot, and proves the verifier itself catches each class of drift.
/// Adding a new upstream version is a drop-in (embed its schema.json + register a
/// <see cref="SchemaParityPolicy"/>); this test then covers it automatically via <see cref="AllEmbeddedVersions"/>.
/// </summary>
public sealed class UpstreamSchemaParityTests
{
    private static readonly UpstreamSchemaRegistry Registry = new();

    public static IEnumerable<object[]> AllEmbeddedVersions() =>
        Registry.AvailableVersions.Select(v => new object[] { v });

    [Fact]
    public void Registry_HasTheUpstream050Snapshot()
    {
        Registry.AvailableVersions.Should().Contain("0.5.0",
            "the frozen upstream v0.5.0 schema must be embedded for self-verification");
    }

    [Theory]
    [MemberData(nameof(AllEmbeddedVersions))]
    public void DotNetSchema_IsCompatibleWith_EveryEmbeddedUpstreamVersion(string version)
    {
        var report = SchemaParityVerifier.VerifyDotNet(version, Registry);

        report.IsCompatible.Should().BeTrue(
            "the .NET schema must stay compatible with upstream v{0}:\n{1}", version, report.Summary());
    }

    [Fact]
    public void Report_RecordsTheDocumentedDivergences_NotAsBreaks()
    {
        var report = SchemaParityVerifier.VerifyDotNet("0.5.0", Registry);

        report.Breaks.Should().BeEmpty();
        // The intentional deltas surface as informational notes (owner_id/invalidated_at supersets,
        // HAS_FACT/IN_SESSION extensions, the User/MemoryReadAudit omissions).
        report.DocumentedDivergences.Should().Contain(d => d.Contains("owner_id"));
        report.DocumentedDivergences.Should().Contain(d => d.Contains("HAS_FACT"));
        report.DocumentedDivergences.Should().Contain(d => d.Contains("User"));
    }

    // ── Verifier engine: each break class is actually detected ───────────────

    private static readonly SchemaParityPolicy Policy = SchemaParityPolicy.Upstream_0_5_0;

    private static SchemaDescriptor Upstream() => Registry.Load("0.5.0");

    private static SchemaDescriptor NetWith(
        IEnumerable<string>? labels = null,
        IEnumerable<string>? rels = null,
        IEnumerable<string>? props = null)
    {
        var baseline = DotNetSchema.Describe();
        return new SchemaDescriptor(
            "net-test",
            (labels ?? baseline.NodeLabels).ToHashSet(StringComparer.Ordinal),
            (rels ?? baseline.RelationshipTypes).ToHashSet(StringComparer.Ordinal),
            (props ?? baseline.Properties).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void Verifier_FlagsMissingUpstreamLabel()
    {
        var net = NetWith(labels: DotNetSchema.Describe().NodeLabels.Where(l => l != "Fact"));
        var report = SchemaParityVerifier.Verify(net, Upstream(), Policy);

        report.IsCompatible.Should().BeFalse();
        report.MissingLabels.Should().Contain("Fact");
    }

    [Fact]
    public void Verifier_FlagsMissingUpstreamRelationshipType()
    {
        var net = NetWith(rels: DotNetSchema.Describe().RelationshipTypes.Where(r => r != "RELATED_TO"));
        var report = SchemaParityVerifier.Verify(net, Upstream(), Policy);

        report.IsCompatible.Should().BeFalse();
        report.MissingRelationshipTypes.Should().Contain("RELATED_TO");
    }

    [Fact]
    public void Verifier_FlagsUndocumentedNetOnlyRelationship()
    {
        var net = NetWith(rels: DotNetSchema.Describe().RelationshipTypes.Append("WIDGET_OF"));
        var report = SchemaParityVerifier.Verify(net, Upstream(), Policy);

        report.IsCompatible.Should().BeFalse();
        report.UndocumentedNetOnlyRelationshipTypes.Should().Contain("WIDGET_OF");
    }

    [Fact]
    public void Verifier_FlagsInteropPropertyDrift_WhenAName_IsRenamed()
    {
        // Simulate a rename: drop the canonical "created_at" (as if it were spelled "createdAt").
        var net = NetWith(props: DotNetSchema.Describe().Properties.Where(p => p != "created_at").Append("createdAt"));
        var report = SchemaParityVerifier.Verify(net, Upstream(), Policy);

        report.IsCompatible.Should().BeFalse();
        report.InteropPropertyDrift.Should().Contain("created_at");
    }

    [Fact]
    public void Verifier_FlagsWhenUpstreamCatchesUpToANetSuperset()
    {
        // Pretend upstream now ships invalidated_at — the .NET divergence note is then stale.
        var upstream = Upstream();
        var upstreamPlus = new SchemaDescriptor(
            upstream.Version, upstream.NodeLabels, upstream.RelationshipTypes,
            upstream.Properties.Append("invalidated_at").ToHashSet(StringComparer.Ordinal));

        var report = SchemaParityVerifier.Verify(DotNetSchema.Describe(), upstreamPlus, Policy);

        report.IsCompatible.Should().BeFalse();
        report.SupersetsUpstreamCaughtUpTo.Should().Contain("invalidated_at");
    }

    [Fact]
    public void Verifier_StructuralGate_CatchesRenameOfASharedProperty_OutsideTheInteropAllowlist()
    {
        // "subtype" is shared with upstream but is NOT in InteropCriticalProperties. The structural gate
        // (mirroring labels/rels) must still catch its rename — otherwise a silent interop break ships.
        var net = NetWith(props: DotNetSchema.Describe().Properties.Where(p => p != "subtype").Append("subType"));
        var report = SchemaParityVerifier.Verify(net, Upstream(), Policy);

        report.IsCompatible.Should().BeFalse();
        report.MissingProperties.Should().Contain("subtype",
            "renaming any shared property away from upstream's spelling must be flagged, allowlisted or not");
    }

    // ── Version ordering (default-to-newest must be semver, not ordinal) ─────

    [Fact]
    public void OrderByVersion_OrdersBySemver_NotOrdinalString()
    {
        var ordered = UpstreamSchemaRegistry.OrderByVersion(new[] { "0.5.0", "0.10.0", "0.5.1", "1.0.0", "0.9.0" });

        ordered.Should().ContainInOrder("0.5.0", "0.5.1", "0.9.0", "0.10.0", "1.0.0");
        ordered[^1].Should().Be("1.0.0", "default-to-newest ([^1]) must resolve to the true latest semver");
    }

    [Fact]
    public void OrderByVersion_PicksTenPointZeroOverFivePointZero_AsNewest()
    {
        // The exact regression: ordinal sort places "0.10.0" before "0.5.0" and would pick 0.5.0 as newest.
        UpstreamSchemaRegistry.OrderByVersion(new[] { "0.5.0", "0.10.0" })[^1].Should().Be("0.10.0");
    }
}
