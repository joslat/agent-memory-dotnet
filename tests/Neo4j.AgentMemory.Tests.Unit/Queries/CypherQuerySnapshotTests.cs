using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Neo4j.AgentMemory.Neo4j.Queries;

namespace Neo4j.AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Snapshot and structural regression tests for all centralized Cypher query constants.
///
/// Snapshot strategy (no external Verify library):
///   - <see cref="CypherCatalog_MatchesSnapshot"/> writes a human-readable .snap file to the
///     source directory on first run (or when UPDATE_CYPHER_SNAPSHOTS=1).  Commit that file.
///   - Subsequent runs compare the live catalog against the committed snapshot; any mutation
///     (content change, addition, deletion) fails the test.
///   - To intentionally update: set env var UPDATE_CYPHER_SNAPSHOTS=1 and re-run once.
/// </summary>
public sealed class CypherQuerySnapshotTests
{
    // ── Snapshot file path lives alongside this source file ──────────────────

    private static readonly string SnapshotFilePath = ResolveSnapshotPath();

    private static string ResolveSnapshotPath([CallerFilePath] string? sourceFile = null)
        => Path.Combine(Path.GetDirectoryName(sourceFile)!, "CypherQuerySnapshot.snap");

    // ── Known Cypher node labels used across all *Queries classes ─────────────

    private static readonly HashSet<string> KnownNodeLabels = new(StringComparer.Ordinal)
    {
        "Conversation", "Entity", "Extractor", "Fact", "Message",
        "Migration", "Preference", "ReasoningStep", "ReasoningTrace",
        "Schema", "Tool", "ToolCall"
    };

    // ── Expected query inventory count ────────────────────────────────────────
    // Update this constant whenever queries are deliberately added or removed.

    private const int ExpectedQueryCount = 137;

    // ── MemberData source ─────────────────────────────────────────────────────

    public static IEnumerable<object[]> GetAllCypherQueries()
        => CypherQueryRegistry.GetAll().Select(q => new object[] { q.Name, q.Cypher });

    // =========================================================================
    // Snapshot regression test
    // =========================================================================

    /// <summary>
    /// Detects any unintentional modification, addition, or deletion of Cypher queries.
    /// The companion file <c>CypherQuerySnapshot.snap</c> must be committed to git.
    /// Set env var <c>UPDATE_CYPHER_SNAPSHOTS=1</c> to regenerate the snapshot.
    /// </summary>
    [Fact]
    public void CypherCatalog_MatchesSnapshot()
    {
        var current = BuildCatalogText();
        bool forceUpdate = string.Equals(
            Environment.GetEnvironmentVariable("UPDATE_CYPHER_SNAPSHOTS"), "1",
            StringComparison.OrdinalIgnoreCase);

        if (!File.Exists(SnapshotFilePath) || forceUpdate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SnapshotFilePath)!);
            File.WriteAllText(SnapshotFilePath, current, Encoding.UTF8);

            if (forceUpdate) return;

            Assert.Fail(
                $"Snapshot file was missing and has been created at:\n  {SnapshotFilePath}\n\n" +
                "Commit this file to establish the Cypher query baseline, then re-run the test.");
        }

        var expected = NormalizeLineEndings(File.ReadAllText(SnapshotFilePath, Encoding.UTF8));
        var actual = NormalizeLineEndings(current);

        actual.Should().Be(expected,
            because:
                "Cypher query content must not change without a deliberate snapshot update.\n" +
                $"If the change is intentional, set UPDATE_CYPHER_SNAPSHOTS=1 and re-run, " +
                $"then commit the updated snapshot at:\n  {SnapshotFilePath}");
    }

    // =========================================================================
    // Query inventory test
    // =========================================================================

    /// <summary>
    /// Guards against accidentally deleted query constants by asserting a fixed total count.
    /// Update <see cref="ExpectedQueryCount"/> when queries are deliberately added or removed.
    /// </summary>
    [Fact]
    public void CypherQueryInventory_CountMatchesExpected()
    {
        var queries = CypherQueryRegistry.GetAll();

        queries.Should().HaveCount(ExpectedQueryCount,
            because:
                $"the catalog must contain exactly {ExpectedQueryCount} Cypher query constants. " +
                "Update CypherQuerySnapshotTests.ExpectedQueryCount if the change was intentional.");
    }

    // =========================================================================
    // Structural validation — Theory tests (one assertion per query)
    // =========================================================================

    /// <summary>
    /// Every query must start with a recognised Cypher keyword.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllCypherQueries))]
    public void CypherQuery_StartsWithValidKeyword(string name, string cypher)
    {
        var trimmed = cypher.TrimStart();

        trimmed.Should().MatchRegex(
            @"^(MATCH|MERGE|CREATE|CALL|WITH|UNWIND|OPTIONAL|RETURN|DELETE|SET|REMOVE|DROP|SHOW|DETACH)",
            because: $"{name} must start with a valid Cypher keyword");
    }

    /// <summary>
    /// Any query that contains a WHERE or SET clause must use $parameter placeholders,
    /// not hardcoded literal values (exempting DDL schema statements).
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllCypherQueries))]
    public void CypherQuery_UsesParameterizedValues_WhenWhereOrSetPresent(string name, string cypher)
    {
        var upper = cypher.ToUpperInvariant();

        bool isDdl = upper.Contains("CREATE INDEX")
            || upper.Contains("CREATE FULLTEXT INDEX")
            || upper.Contains("CREATE VECTOR INDEX")
            || upper.Contains("CREATE POINT INDEX")
            || upper.Contains("CREATE CONSTRAINT")
            || upper.Contains("DROP INDEX")
            || upper.Contains("DROP CONSTRAINT")
            || upper.Contains("REQUIRE ");

        if (isDdl) return;

        bool hasFilterOrMutation =
            Regex.IsMatch(upper, @"\bWHERE\b") ||
            Regex.IsMatch(upper, @"\bSET\b");

        if (hasFilterOrMutation)
        {
            cypher.Should().Contain("$",
                because:
                    $"{name} contains WHERE or SET — all variable values must use $parameter syntax");
        }
    }

    /// <summary>
    /// Node labels referenced in pattern positions must belong to the known domain schema.
    /// This catches typos and orphaned references to labels that no longer exist.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllCypherQueries))]
    public void CypherQuery_OnlyReferencesKnownNodeLabels(string name, string cypher)
    {
        // Match "(varname:Label" or "(:Label" — node pattern label references only
        var labelPattern = new Regex(@"\(\w*:([\w]+)");
        var matches = labelPattern.Matches(cypher);

        foreach (Match match in matches)
        {
            var label = match.Groups[1].Value;
            KnownNodeLabels.Should().Contain(label,
                because:
                    $"{name} references node label '{label}' which is not in the known domain schema. " +
                    "Add it to KnownNodeLabels if it is intentional.");
        }
    }

    /// <summary>
    /// Every query must have balanced parentheses — a structural indicator of well-formed Cypher.
    /// </summary>
    [Theory]
    [MemberData(nameof(GetAllCypherQueries))]
    public void CypherQuery_HasBalancedParentheses(string name, string cypher)
    {
        int depth = 0;
        int lineNumber = 1;

        foreach (char c in cypher)
        {
            if (c == '\n') lineNumber++;
            else if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                depth.Should().BeGreaterThanOrEqualTo(0,
                    because: $"{name} has an unmatched closing parenthesis near line {lineNumber}");
            }
        }

        depth.Should().Be(0,
            because: $"{name} has {depth} unmatched opening parenthesis/parentheses");
    }

    // =========================================================================
    // Helpers
    // =========================================================================

    private static string BuildCatalogText()
    {
        var queries = CypherQueryRegistry.GetAll()
            .OrderBy(q => q.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sb = new StringBuilder();
        sb.AppendLine($"# Cypher Query Snapshot — {queries.Count} queries");
        sb.AppendLine("# Auto-generated by CypherQuerySnapshotTests.");
        sb.AppendLine("# To regenerate: set UPDATE_CYPHER_SNAPSHOTS=1 and re-run the test.");
        sb.AppendLine();

        foreach (var (queryName, cypher) in queries)
        {
            sb.AppendLine($"## {queryName}");
            sb.AppendLine(cypher.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private static string NormalizeLineEndings(string text)
        => text.Replace("\r\n", "\n").Replace("\r", "\n");
}
