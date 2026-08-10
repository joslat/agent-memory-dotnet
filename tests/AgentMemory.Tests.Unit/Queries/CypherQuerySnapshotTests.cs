using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

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
        "ConsolidationRun", "Conversation", "Entity", "Extractor", "Fact", "MemoryReadAudit", "Message",
        "Migration", "Preference", "ReasoningStep", "ReasoningTrace",
        "Schema", "Tool", "ToolCall"
    };

    // ── Expected query inventory count ────────────────────────────────────────
    // Update this constant whenever queries are deliberately added or removed.

    private const int ExpectedQueryCount = 154; // +SchemaQueries.FactOwnerKeyIndex (measured: FindDuplicate runs on every fact write and had NO index entry point, planning a full 20,000-row scan; this seeks 100). // +SchemaQueries.MessageSessionIndex (measured: Neo4j will not seek a composite from a leading-column predicate alone, so 0007's (session_id, timestamp) gave GetRecentBySession nothing). // +SchemaQueries.MessageSessionTimestampIndex (Message.session_id is the predicate of the PRIMARY short-term recall path -- MessageQueries.cs:201, run on essentially every turn -- and nothing indexed it, so the plan was proportional to every message in the store rather than to the session; composite because TemporalQueries.cs:99-103 adds a trailing timestamp range to the same equality). // +SchemaQueries.FactPredicateKeyIndex (the same shape one column over: predicate_key sits at column 3 of fact_merge_key_idx and Neo4j serves a composite only on a matching PREFIX, so FactQueries.cs:88 had no index entry point at all -- a full :Fact scan across all owners whenever relation-completeness retrieval fires). // +SchemaQueries.MemoryReadAuditMemoryIdIndex (BUG-A2: HistoryQueries OPTIONAL MATCHes MemoryReadAudit on memory_id, which nothing indexed, so every history row scanned the whole label -- and the label grows ~25 rows per recall, so it degrades with TIME rather than data size). // +SchemaQueries.FactMergeKeyIndex (L11: every fact MERGE was an all-:Fact label scan; the composite {subject_key, object_key, predicate_key, owner_key} index is what makes the range-index key cap real for facts, which is why IndexKeyBudget.EnsureCompositeIndexable lands with it). // -1: SearchByCanonicalPredicates became an owner-conditional *method* (excluded, like GetBySubject) when the audit found it ignored IncludeShared and coerced a null-owner scope to the shared bucket. // +FactQueries.SelectFactsMissingCanonicalKeys/ApplyCanonicalKeys (Phase 1.1: canonical identity needs a C#-driven backfill, since Cypher's toLower diverges from ToLowerInvariant on U+0130). // +FactQueries.SearchByCanonicalPredicates (G3B.13: top-K is a relevance cutoff and cannot answer "how many", so a relation is retrieved whole via predicate_key). // +SchemaQueries.ShowIndexStates (BUG-S1: only vector-index dimensions were validated at bootstrap, so a FAILED range index degraded silently into full scans). base + ConsolidationQueries/SchemaQueries/TOUCHED/ConflictQueries consts. +MessageQueries.GetAllBySession (cycle-3); +SchemaQueries.ShowConstraintNames/ShowIndexNames (schema-check CLI); +SchemaPersistenceQueries.Save/DeactivateByName/LoadActiveByName/LoadByNameVersion/List/Exists/DeleteById (G4 schema-node CRUD, 7); +MemoryReadAudit constraint/index. Owner-conditional queries are *methods* (excluded): EntityQueries.ApplyConfidenceDelta/Delete/MergeEntities/SearchByLocation/SearchInBoundingBox/GetByType/FindSimilarByEmbedding, FactQueries.Delete/FindByTriple, PreferenceQueries.Delete, DecayQueries.PruneEntities/PruneFacts/PrunePreferences, ExtractorQueries.GetEntityProvenance, ReasoningQueries.ListTracesBySession (R2 owner-scoped 2026-06-13), ReasoningQueries.DeleteBySession (R6-C owner-scoped 2026-06-20: const→method, −1); +PreferenceQueries.UpsertBatch/RelationshipQueries.UpsertBatch (feat-04, +2).

    // ── MemberData source ─────────────────────────────────────────────────────

    // W1.1 adds one fused statement for each node memory kind.
    private const int FusedPersistenceQueryCount = 3;

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

        queries.Should().HaveCount(ExpectedQueryCount + FusedPersistenceQueryCount,
            because:
                $"the catalog must contain exactly {ExpectedQueryCount + FusedPersistenceQueryCount} Cypher query constants. " +
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
