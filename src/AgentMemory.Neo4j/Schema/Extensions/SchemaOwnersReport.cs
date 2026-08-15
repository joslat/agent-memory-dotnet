using System.Globalization;
using System.Text;
using AgentMemory.Neo4j.Schema.Parity;

namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// Answers "whose shape is this?" for every schema shape that is not base — and fails when nothing can.
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap this closes.</b> <see cref="SchemaParityVerifier"/> answers "is this shape allowed?" and
/// stops there. <c>trace_kind</c> shipped in migration 0011 with its entire rationale in a Cypher
/// comment: the parity policy, the CLI and the docs knew nothing about which feature owned it. One
/// feature made that survivable. Five would not.
/// </para>
/// <para>
/// <b>What counts as an orphan, and what deliberately does not.</b> Two things fail this report: a
/// divergence the effective policy allows that no registered extension declares (the extension's
/// parity delta and its declarations have drifted apart), and an applied <c>ext/&lt;id&gt;/…</c>
/// migration whose id this binary does not know (the database carries schema from a module that is no
/// longer registered — a downgrade, or a removed extension).
/// </para>
/// <para>
/// Live relationship types and labels are deliberately <b>not</b> scanned. On a shared database those
/// belong to other applications, and counting them would make this check impossible to pass there —
/// the same reasoning that already excludes foreign indexes from the conformance check. This report
/// only judges shapes AgentMemory itself claims.
/// </para>
/// </remarks>
internal sealed record SchemaOwnersReport(
    string UpstreamVersion,
    IReadOnlyList<SchemaShapeOwner> Shapes,
    IReadOnlyList<AppliedExtensionMigration> AppliedExtensionMigrations,
    IReadOnlyList<string> Orphans,
    IReadOnlyList<ISchemaExtension> Active)
{
    /// <summary>The sentinel owner for everything the base schema and base policy carry.</summary>
    public const string BaseOwner = "base";

    /// <summary>True when every non-base shape names an owner this binary knows.</summary>
    public bool HasOwners => Orphans.Count == 0;

    /// <summary>
    /// Builds the report from the registry, the configured extension set, and the migration rows read
    /// from the database.
    /// </summary>
    /// <param name="registry">Every registered extension. Registration, not activation.</param>
    /// <param name="activeIds">The ids <c>Neo4jOptions.Extensions</c> asked for.</param>
    /// <param name="appliedMigrationVersions">
    /// The <c>(:Migration).version</c> values in the database, base and extension alike. Empty is a
    /// legitimate input — the report still describes what the binary would own.
    /// </param>
    public static SchemaOwnersReport Build(
        SchemaExtensionRegistry registry,
        IEnumerable<string> activeIds,
        IReadOnlyDictionary<string, string?> appliedMigrationVersions)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(activeIds);
        ArgumentNullException.ThrowIfNull(appliedMigrationVersions);

        var basePolicy = SchemaParityPolicy.Upstream_0_5_0;
        var active = registry.Active(activeIds);
        var effective = basePolicy.WithExtensions(active);

        var shapes = new List<SchemaShapeOwner>();
        var orphans = new List<string>();

        // Every REGISTERED extension is described, not only the active ones. An extension's shapes
        // remain in a database after it is switched off (deactivation is not a down-migration -- the
        // schema is additive and harmless), so a report that only described active ones would stop
        // naming an owner precisely when someone is trying to work out where a leftover came from.
        foreach (var extension in registry.All)
        {
            var isActive = active.Contains(extension);

            foreach (var label in extension.DeclaredLabels.OrderBy(x => x, StringComparer.Ordinal))
                shapes.Add(new SchemaShapeOwner("label", label, extension.Id, isActive));

            foreach (var type in extension.DeclaredRelationshipTypes.OrderBy(x => x, StringComparer.Ordinal))
                shapes.Add(new SchemaShapeOwner("relationship", type, extension.Id, isActive));

            foreach (var (label, properties) in extension.DeclaredProperties.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                foreach (var property in properties.OrderBy(x => x, StringComparer.Ordinal))
                    shapes.Add(new SchemaShapeOwner("property", $"{label}.{property}", extension.Id, isActive));
            }

            foreach (var version in extension.BaseResidentMigrations.OrderBy(x => x, StringComparer.Ordinal))
                shapes.Add(new SchemaShapeOwner("migration", version, extension.Id, IsActive: true));
        }

        // Divergence-without-a-declaration. The effective policy is base plus the active deltas, so
        // anything here that base did not carry MUST trace back to an extension that also declared the
        // shape. When it does not, the extension's delta and its declarations have drifted apart and
        // the allowlist has grown an entry nobody can attribute.
        CheckDivergence(
            "net-only label", effective.NetOnlyLabels, basePolicy.NetOnlyLabels,
            active.SelectMany(e => e.DeclaredLabels), orphans);
        CheckDivergence(
            "net-only relationship", effective.NetOnlyRelationshipTypes, basePolicy.NetOnlyRelationshipTypes,
            active.SelectMany(e => e.DeclaredRelationshipTypes), orphans);
        CheckDivergence(
            "net superset property", effective.NetSupersetProperties, basePolicy.NetSupersetProperties,
            active.SelectMany(e => e.DeclaredProperties.Values.SelectMany(p => p)), orphans);

        var appliedExtensionMigrations = new List<AppliedExtensionMigration>();
        var prefix = MigrationRunnerExtensionPrefix;
        foreach (var (version, appliedAtUtc) in appliedMigrationVersions.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            if (!version.StartsWith(prefix, StringComparison.Ordinal)) continue;

            var remainder = version[prefix.Length..];
            var slash = remainder.IndexOf('/', StringComparison.Ordinal);
            var ownerId = slash > 0 ? remainder[..slash] : remainder;
            var known = registry.Find(ownerId) is not null;

            appliedExtensionMigrations.Add(new AppliedExtensionMigration(version, ownerId, appliedAtUtc, known));
            if (!known)
            {
                orphans.Add(
                    $"applied migration '{version}' belongs to extension '{ownerId}', which is not "
                    + "registered in this build. The database carries schema from a module this binary "
                    + "does not know — a downgrade, or an extension that was removed.");
            }
        }

        return new SchemaOwnersReport(
            basePolicy.UpstreamVersion,
            shapes,
            appliedExtensionMigrations,
            orphans,
            active);
    }

    private const string MigrationRunnerExtensionPrefix = "ext/";

    private static void CheckDivergence(
        string kind,
        IReadOnlySet<string> effective,
        IReadOnlySet<string> baseline,
        IEnumerable<string> declared,
        List<string> orphans)
    {
        var declaredSet = declared.ToHashSet(StringComparer.Ordinal);
        foreach (var entry in effective.Except(baseline).OrderBy(x => x, StringComparer.Ordinal))
        {
            if (declaredSet.Contains(entry)) continue;
            orphans.Add(
                $"{kind} '{entry}' is allowed by the effective parity policy but declared by no active "
                + "extension. A divergence with no owner is exactly the drift this report exists to "
                + "refuse.");
        }
    }

    /// <summary>The operator-facing rendering, including the failure lines when there are orphans.</summary>
    public string Render()
    {
        var builder = new StringBuilder();
        var activeDescription = Active.Count == 0
            ? "none"
            : string.Join(", ", Active.Select(e => $"{e.Id} v{e.Version.ToString(CultureInfo.InvariantCulture)}"));
        builder.AppendLine(CultureInfo.InvariantCulture,
            $"schema-check: policy base {UpstreamVersion} + extensions: [{activeDescription}]");

        foreach (var shape in Shapes)
        {
            var suffix = shape.IsActive ? string.Empty : "   (registered, not active)";
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  {shape.Kind,-12} {shape.Name,-40} owner: {shape.Owner}{suffix}");
        }

        foreach (var migration in AppliedExtensionMigrations)
        {
            var applied = string.IsNullOrEmpty(migration.AppliedAtUtc) ? "(no timestamp)" : migration.AppliedAtUtc;
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"  {"applied",-12} {migration.Version,-40} owner: {migration.Owner}  {applied}");
        }

        if (Orphans.Count == 0)
        {
            builder.AppendLine(CultureInfo.InvariantCulture,
                $"schema-check: every non-base shape names an owner ({Shapes.Count} shape(s) attributed).");
            return builder.ToString();
        }

        builder.AppendLine(CultureInfo.InvariantCulture,
            $"schema-check: FAILED — {Orphans.Count} schema shape(s) have no owner:");
        foreach (var orphan in Orphans)
            builder.AppendLine(CultureInfo.InvariantCulture, $"  - {orphan}");

        return builder.ToString();
    }
}

/// <summary>One schema shape and the extension that owns it.</summary>
internal sealed record SchemaShapeOwner(string Kind, string Name, string Owner, bool IsActive);

/// <summary>An <c>ext/&lt;id&gt;/…</c> migration recorded in the database.</summary>
internal sealed record AppliedExtensionMigration(
    string Version, string Owner, string? AppliedAtUtc, bool OwnerIsRegistered);
