namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Lightweight fluent builder for composing Cypher queries with optional clauses.
/// Not a full AST — designed for the 3-5 queries that need dynamic WHERE/ORDER/LIMIT.
/// </summary>
/// <remarks>
/// Each method returns a new immutable instance, making instances safe to share across threads.
/// Parameters always use <c>$paramName</c> syntax — never interpolated values.
/// </remarks>
public sealed class CypherBuilder
{
    private readonly IReadOnlyList<string> _lines;
    private readonly bool _whereStarted;

    /// <summary>Creates an empty builder.</summary>
    public CypherBuilder()
    {
        _lines = Array.Empty<string>();
        _whereStarted = false;
    }

    private CypherBuilder(IReadOnlyList<string> lines, bool whereStarted)
    {
        _lines = lines;
        _whereStarted = whereStarted;
    }

    // ── Static factory methods ─────────────────────────────────────────────────

    /// <summary>Start a query with a MATCH clause.</summary>
    public static CypherBuilder Match(string pattern)
        => new(new[] { "MATCH " + pattern }, whereStarted: false);

    /// <summary>Start a query with a CALL clause.</summary>
    public static CypherBuilder Call(string procedure)
        => new(new[] { "CALL " + procedure }, whereStarted: false);

    // ── Structural clauses ─────────────────────────────────────────────────────

    /// <summary>Append an OPTIONAL MATCH clause.</summary>
    public CypherBuilder OptionalMatch(string pattern)
        => Append("OPTIONAL MATCH " + pattern);

    /// <summary>Append a WITH clause.</summary>
    public CypherBuilder With(string clause)
        => Append("WITH " + clause);

    /// <summary>Append an UNWIND clause.</summary>
    public CypherBuilder Unwind(string clause)
        => Append("UNWIND " + clause);

    /// <summary>Append a SET clause.</summary>
    public CypherBuilder Set(string clause)
        => Append("SET " + clause);

    /// <summary>Append a RETURN clause.</summary>
    public CypherBuilder Return(string clause)
        => Append("RETURN " + clause);

    // ── WHERE / AND / OR ──────────────────────────────────────────────────────

    /// <summary>
    /// Append a WHERE condition. The first condition emits <c>WHERE</c>;
    /// subsequent conditions emit <c>AND</c>. Skipped when <paramref name="when"/> is false.
    /// </summary>
    public CypherBuilder Where(string condition, bool when = true)
        => AddCondition(condition, "AND", when);

    /// <summary>
    /// Append an AND condition (or WHERE if no condition exists yet).
    /// Skipped when <paramref name="when"/> is false.
    /// </summary>
    public CypherBuilder And(string condition, bool when = true)
        => AddCondition(condition, "AND", when);

    /// <summary>
    /// Append an OR condition (or WHERE if no condition exists yet).
    /// Skipped when <paramref name="when"/> is false.
    /// </summary>
    public CypherBuilder Or(string condition, bool when = true)
        => AddCondition(condition, "OR", when);

    // ── Ordering and pagination ────────────────────────────────────────────────

    /// <summary>Append an ORDER BY clause. Skipped when <paramref name="when"/> is false.</summary>
    public CypherBuilder OrderBy(string clause, bool when = true)
        => when ? Append("ORDER BY " + clause) : this;

    /// <summary>Append a SKIP clause. Skipped when <paramref name="when"/> is false.</summary>
    public CypherBuilder Skip(string param, bool when = true)
        => when ? Append("SKIP " + param) : this;

    /// <summary>Append a LIMIT clause. Skipped when <paramref name="when"/> is false.</summary>
    public CypherBuilder Limit(string param, bool when = true)
        => when ? Append("LIMIT " + param) : this;

    // ── Vector search ──────────────────────────────────────────────────────────

    /// <summary>
    /// Append a CALL+YIELD block for a vector similarity search query.
    /// </summary>
    /// <param name="indexName">The vector index name.</param>
    /// <param name="embeddingParam">
    /// Cypher expression for the query vector, e.g. <c>$embedding</c> or <c>node.embedding</c>.
    /// </param>
    /// <param name="nodeAlias">Alias for the yielded node.</param>
    /// <param name="topK">Maximum number of candidate results from the index.</param>
    public CypherBuilder WithVectorSearch(string indexName, string embeddingParam, string nodeAlias, int topK)
    {
        var lines = new List<string>(_lines)
        {
            $"CALL db.index.vector.queryNodes('{indexName}', {topK}, {embeddingParam})",
            $"YIELD {nodeAlias}, score"
        };
        return new CypherBuilder(lines, _whereStarted);
    }

    // ── Raw fragment escape hatch ──────────────────────────────────────────────

    /// <summary>
    /// Append a pre-formatted Cypher fragment verbatim (e.g., output from MetadataFilterBuilder).
    /// Multi-line fragments are split and each non-empty line is appended as a separate clause.
    /// Skipped when the fragment is null, whitespace, or <paramref name="when"/> is false.
    /// </summary>
    public CypherBuilder AndRawFragment(string? fragment, bool when = true)
    {
        if (!when || string.IsNullOrWhiteSpace(fragment))
            return this;

        var fragmentLines = fragment
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        if (fragmentLines.Length == 0) return this;

        var newLines = new List<string>(_lines);
        newLines.AddRange(fragmentLines);
        return new CypherBuilder(newLines, _whereStarted);
    }

    // ── Build ──────────────────────────────────────────────────────────────────

    /// <summary>Returns the composed Cypher query as a single string with newline separators.</summary>
    public string Build()
        => string.Join(Environment.NewLine, _lines);

    // ── Private helpers ────────────────────────────────────────────────────────

    private CypherBuilder AddCondition(string condition, string connector, bool when)
    {
        if (!when) return this;
        var prefix = _whereStarted ? connector : "WHERE";
        var lines = new List<string>(_lines) { $"{prefix} {condition}" };
        return new CypherBuilder(lines, whereStarted: true);
    }

    private CypherBuilder Append(string line)
        => new(new List<string>(_lines) { line }, _whereStarted);
}
