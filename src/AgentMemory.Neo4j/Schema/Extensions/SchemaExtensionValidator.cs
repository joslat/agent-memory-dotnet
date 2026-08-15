using System.Text.RegularExpressions;
using AgentMemory.Neo4j.Schema.Parity;

namespace AgentMemory.Neo4j.Schema.Extensions;

/// <summary>
/// Enforces <b>R1 — schema-additive-only</b>: an extension may add shapes, never rename, retype, or
/// repurpose a base one.
/// </summary>
/// <remarks>
/// <para>
/// Shared deliberately between the registry (which runs the id and dependency checks at startup) and
/// the unit suite (which runs the full disjointness and migration-lint checks on every build). A rule
/// that lives only in a test is a rule the next extension author discovers by breaking production; a
/// rule that lives only in the registry costs a startup to find out. This is one implementation with
/// two callers, the <c>SchemaParityVerifier</c> precedent.
/// </para>
/// <para>
/// The declarations of <i>every registered</i> extension are checked, active or not. An extension that
/// collides with base is broken whether or not anyone has switched it on yet, and finding that at build
/// time is the entire value.
/// </para>
/// </remarks>
internal static class SchemaExtensionValidator
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9-]*$", RegexOptions.Compiled);

    /// <summary>
    /// Statements a migration under the <c>ext/</c> namespace is allowed to be. Base migrations are
    /// exempt — the lint applies to the extension namespace only, because base is reviewed as base.
    /// </summary>
    private static readonly Regex AllowedStatement = new(
        @"^\s*(CREATE\s+(INDEX|CONSTRAINT|VECTOR\s+INDEX|FULLTEXT\s+INDEX)\b.*IF\s+NOT\s+EXISTS\b|MATCH\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ForbiddenStatement = new(
        @"\b(DROP|REMOVE|DETACH\s+DELETE|DELETE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Validates one extension's identity in isolation. Cheap; run at startup.</summary>
    public static IReadOnlyList<string> ValidateIdentity(ISchemaExtension extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(extension.Id) || !IdPattern.IsMatch(extension.Id))
        {
            problems.Add(
                $"extension id '{extension.Id}' must be lowercase-kebab ([a-z][a-z0-9-]*) — it is "
                + "load-bearing inside (:Migration).version keys, so it must be path-safe and stable.");
        }

        if (extension.Version < 1)
            problems.Add($"extension '{extension.Id}' must declare Version >= 1; got {extension.Version}.");

        foreach (var script in extension.MigrationScripts)
        {
            if (!script.EndsWith(".cypher", StringComparison.Ordinal) || script.Contains('/', StringComparison.Ordinal) ||
                script.Contains('\\', StringComparison.Ordinal))
            {
                problems.Add(
                    $"extension '{extension.Id}' migration script '{script}' must be a bare "
                    + "'000N_name.cypher' filename; the ext/<id>/ prefix is supplied by the runner.");
            }
        }

        if (extension.TckProfile.IsDeclared && extension.TckProfile.MinimumCaseCount < 1)
        {
            problems.Add(
                $"extension '{extension.Id}' declares TCK case folder '{extension.TckProfile.CaseFolder}' "
                + "with MinimumCaseCount 0 — a declared profile that requires no cases proves nothing. "
                + "Use TckProfileDescriptor.None when no profile has shipped.");
        }

        return problems;
    }

    /// <summary>
    /// The full R1 check across a set of extensions and the base schema: nothing an extension declares
    /// may already belong to base, and no two extensions may declare the same shape.
    /// </summary>
    /// <param name="extensions">Every REGISTERED extension, not just the active ones.</param>
    /// <param name="baseSchema">The base descriptor, normally <see cref="DotNetSchema.Describe"/>.</param>
    /// <param name="basePolicy">The base parity policy, used to validate label-adoption deltas.</param>
    public static IReadOnlyList<string> ValidateDeclarations(
        IReadOnlyList<ISchemaExtension> extensions,
        SchemaDescriptor baseSchema,
        SchemaParityPolicy basePolicy)
    {
        ArgumentNullException.ThrowIfNull(extensions);
        ArgumentNullException.ThrowIfNull(baseSchema);
        ArgumentNullException.ThrowIfNull(basePolicy);

        var problems = new List<string>();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var extension in extensions)
        {
            problems.AddRange(ValidateIdentity(extension));

            foreach (var relationshipType in extension.DeclaredRelationshipTypes)
            {
                if (baseSchema.RelationshipTypes.Contains(relationshipType))
                {
                    problems.Add(
                        $"extension '{extension.Id}' declares relationship type '{relationshipType}', "
                        + "which base already owns. R1 forbids repurposing a base shape.");
                }

                Claim(problems, owners, $"rel:{relationshipType}", extension.Id);
            }

            foreach (var label in extension.DeclaredLabels)
            {
                // THE ONE LEGAL OVERLAP. A label that exists upstream but not in .NET may be adopted by
                // an extension -- working-memory adopting :User -- and adoption is exactly the pairing of
                // a DeclaredLabels entry with a RemoveUpstreamOnlyLabels entry. Without the pairing it is
                // an undeclared label grab, which is what the parity gate exists to refuse.
                var adopted = extension.ParityDelta.RemoveUpstreamOnlyLabels.Contains(label);
                if (baseSchema.NodeLabels.Contains(label) && !adopted)
                {
                    problems.Add(
                        $"extension '{extension.Id}' declares label '{label}', which base already owns.");
                }

                if (adopted && !basePolicy.UpstreamOnlyLabels.Contains(label))
                {
                    problems.Add(
                        $"extension '{extension.Id}' adopts upstream-only label '{label}', but the base "
                        + "policy does not list it as upstream-only. The delta is stale — either the "
                        + "label was already adopted, or it never existed upstream.");
                }

                Claim(problems, owners, $"label:{label}", extension.Id);
            }

            foreach (var (label, properties) in extension.DeclaredProperties)
            {
                foreach (var property in properties)
                {
                    // Properties are deliberately NOT checked against base property names in general:
                    // an extension writing a base property onto its own label would be legitimate. What
                    // is forbidden is two extensions claiming ownership of the same (label, property).
                    Claim(problems, owners, $"prop:{label}.{property}", extension.Id);
                }
            }
        }

        return problems;
    }

    /// <summary>
    /// Lints one extension migration script. Every statement must be an idempotent schema creation or a
    /// <c>MATCH</c>-scoped backfill; anything that could remove or rewrite existing shape is refused.
    /// </summary>
    /// <remarks>
    /// This is what keeps future extension scripts additive without relying on review vigilance. The
    /// base sequence is exempt: it is allowed to do things an optional module is not, and it is reviewed
    /// on those terms.
    /// </remarks>
    public static IReadOnlyList<string> LintMigrationScript(
        string extensionId, string scriptName, string cypher)
    {
        var problems = new List<string>();

        foreach (var statement in Infrastructure.MigrationRunner.ParseStatements(cypher))
        {
            if (ForbiddenStatement.IsMatch(statement))
            {
                problems.Add(
                    $"ext/{extensionId}/{scriptName}: statement removes or rewrites existing shape "
                    + $"(R1 is additive-only): {Excerpt(statement)}");
                continue;
            }

            if (!AllowedStatement.IsMatch(statement))
            {
                problems.Add(
                    $"ext/{extensionId}/{scriptName}: statement is neither a 'CREATE … IF NOT EXISTS' "
                    + $"schema object nor a MATCH-scoped backfill: {Excerpt(statement)}");
            }
        }

        return problems;
    }

    private static void Claim(
        List<string> problems, Dictionary<string, string> owners, string shape, string extensionId)
    {
        if (owners.TryGetValue(shape, out var existing) && !string.Equals(existing, extensionId, StringComparison.Ordinal))
        {
            problems.Add(
                $"shape '{shape}' is declared by both '{existing}' and '{extensionId}'. Every shape has "
                + "exactly one owner — that is what makes the owners report answerable.");
            return;
        }

        owners[shape] = extensionId;
    }

    private static string Excerpt(string statement) =>
        statement.Length <= 120 ? statement : statement[..120] + "…";
}
