using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Neo4j.Queries;

/// <summary>
/// Traversal context reads (CMP-1). Labels, properties and relationship types come from validated manifests only, never from a
/// request; values are parameters. Live records only, in the scope the host's isolation policy resolved.
/// </summary>
[Experimental("AMEXT001")]
internal static class ModuleTraversalQueries
{
    private const string Scope = "($owner IS NULL OR {0}.owner_id=$owner OR ($shared AND {0}.owner_id IS NULL))";

    private static string In(string variable) => string.Format(System.Globalization.CultureInfo.InvariantCulture, Scope, variable);

    // A stored value as the store's filter reads it: lower case (Cypher's folding, which meets every pair the walk's ASCII
    // folding makes equal), with '\' as '/' (Cypher's '\\' is one backslash).
    private static string Slashed(string value) => "replace(toLower(" + value + @"), '\\', '/')";

    /// <summary>
    /// Every live record in scope whose anchor property could match (review 2, 3): a superset of the walk's own matches, never
    /// cut before matching. Both sides are lowered here, so two values the walk folds to the same text always meet, whatever
    /// else Cypher folds. Exact: the value holds an anchor as the walk normalizes it (<c>$values</c>); prefix: it holds one of an
    /// anchor's folders (<c>$folders</c>); glob: that, or it holds a wildcard; name: it holds an anchor's simple name
    /// (<c>$names</c>). The walk then matches each record exactly, by its own rules.
    /// </summary>
    internal static string MatchCandidates(string label, string property, Modules.ModuleAnchorMatch match)
    {
        var p = "n.`" + property + "`";
        var filter = match switch
        {
            Modules.ModuleAnchorMatch.Exact => "ANY(v IN $values WHERE " + Slashed(p) + " CONTAINS toLower(v))",
            Modules.ModuleAnchorMatch.Prefix => "ANY(f IN $folders WHERE " + Slashed(p) + " CONTAINS toLower(f))",
            Modules.ModuleAnchorMatch.Glob => "(" + p + " CONTAINS '*' OR " + p + " CONTAINS '?' OR ANY(f IN $folders WHERE " + Slashed(p) + " CONTAINS toLower(f)))",
            _ => "ANY(s IN $names WHERE " + p + " CONTAINS s)",
        };
        return "MATCH (n:`" + label + "`) WHERE n.invalidated_at IS NULL AND " + In("n") + " AND " + p + " IS NOT NULL AND " + filter + " "
            + "RETURN properties(n) AS row ORDER BY n.key, n.id LIMIT $take";
    }

    /// <summary>
    /// Out: the starting records with more than <c>$maxLinks</c> live targets in scope through the relation (review 2, 3), with
    /// that count, as the in-memory store counts them. The store's degree is read first; only a record whose degree is past the
    /// limit has its live targets counted, at most <c>$over</c> of them.
    /// </summary>
    internal static string OutHubs(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (s:`" + source + "` {id:fid}) WITH fid, s WHERE COUNT { (s)-[:`" + relation + "`]->() } > $maxLinks "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->(old:`" + target + "`) MATCH (t:`" + target + "` {key_id:old.key_id}) "
        + "WHERE t.invalidated_at IS NULL AND " + In("t") + " WITH DISTINCT t LIMIT $over RETURN count(t) AS links } "
        + "WITH fid, s, links WHERE links > $maxLinks RETURN fid, s.key AS key, links";

    /// <summary>In: the starting records with more than <c>$maxLinks</c> live sources in scope linking to any version (see <see cref="OutHubs"/>).</summary>
    internal static string InHubs(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (x:`" + target + "` {id:fid}) "
        // Each version bound first, then its degree counted with an open end, which the store reads without the links.
        + "CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) RETURN sum(COUNT { (v)<-[:`" + relation + "`]-() }) AS degree } "
        + "WITH fid, x, degree WHERE degree > $maxLinks "
        + "CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) MATCH (s:`" + source + "`)-[:`" + relation + "`]->(v) "
        + "WHERE s.invalidated_at IS NULL AND " + In("s") + " WITH DISTINCT s LIMIT $over RETURN count(s) AS links } "
        + "WITH fid, x, links WHERE links > $maxLinks RETURN fid, x.key AS key, links";

    /// <summary>
    /// Out: each live source's link targets, as their live versions (a link may point at an older version of its target; the
    /// live one shares its key identity, <c>key_id</c>), ranked (see <see cref="Ranked"/>); a target's degree is how many live
    /// sources in scope link to it, counted up to <c>$scan</c>.
    /// </summary>
    /// <remarks>
    /// <b>Every target of a source within the link limit is ranked</b> (review 2): a record past <c>$maxLinks</c> is left out
    /// before this read (<see cref="OutHubs"/>), so the cost stays bounded, and the targets ranked are all of them.
    /// </remarks>
    internal static string Out(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (s:`" + source + "` {id:fid}) WHERE s.invalidated_at IS NULL "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->(old:`" + target + "`) " + Live("old", "t", target) + " "
        + "WITH t WHERE " + In("t") + " RETURN DISTINCT t } "
        + "CALL { WITH t MATCH (:`" + target + "` {key_id:t.key_id})<-[:`" + relation + "`]-(o:`" + source + "`) "
        + "WHERE o.invalidated_at IS NULL AND " + In("o") + " WITH DISTINCT o LIMIT $scan RETURN count(o) AS degree } "
        + Ranked("t");

    // The live version of a linked record (review 3, performance): the record itself when the link points at it, else the live
    // version of its key, by the indexed key identity. A key has at most one live version, so the two agree; the first needs
    // no index seek, and a fresh snapshot's links all point at live versions.
    private static string Live(string linked, string live, string label) =>
        "CALL { WITH " + linked + " WITH " + linked + " WHERE " + linked + ".invalidated_at IS NULL RETURN " + linked + " AS " + live + " "
        + "UNION ALL WITH " + linked + " WITH " + linked + " WHERE " + linked + ".invalidated_at IS NOT NULL "
        + "MATCH (" + live + ":`" + label + "` {key_id:" + linked + ".key_id}) WHERE " + live + ".invalidated_at IS NULL RETURN " + live + " }";

    /// <summary>
    /// In: the live sources in scope whose link points at any version of each live target, so links written before the target
    /// changed still count (every version shares its key identity, <c>key_id</c>, which is indexed), ranked (see
    /// <see cref="Ranked"/>); a source's degree is how many links of the relation it has, its own links, counted up to
    /// <c>$scan</c> without reading the targets, as the in-memory store counts a record's links.
    /// </summary>
    /// <remarks>
    /// Review 3 counted distinct live targets in scope instead, reading every target of every neighbour: the walk's p95 rose from
    /// 34 to 54 ms on this repository's code map, results unchanged. A pack's records link only to records that are live (each
    /// import writes a new version of a record whose links changed), so the two counts agree on every pack; they differ only for
    /// a record written by ingestion that still links to a key invalidated since, and there the record's own links are what it
    /// says it covers. The spec defines the degree this way (pack format v1.2 §6.1).
    /// </remarks>
    internal static string In(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (x:`" + target + "` {id:fid}) WHERE x.invalidated_at IS NULL "
        + "CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) "
        + "MATCH (s:`" + source + "`)-[:`" + relation + "`]->(v) WHERE s.invalidated_at IS NULL AND " + In("s") + " "
        + "RETURN DISTINCT s } "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->() WITH 1 AS one LIMIT $scan RETURN count(*) AS degree } "
        + Ranked("s");

    /// <summary>
    /// Per starting record, the reached records ranked by how many of the question's <c>$words</c> their text (the <c>$text</c>
    /// properties, ASCII-folded with <c>$fold</c>) holds, then the lowest degree, then key and version id; the caller takes the
    /// first <c>$fanOut</c> in the format's order (review 3: the cut inside Cypher depended on the store's string order, and its
    /// folding counted words the walk did not).
    /// </summary>
    /// <remarks>
    /// What is returned is a superset of what the cut keeps, and small (review 3, performance: every tie with its properties was
    /// returned, hundreds of rows for a hub of alike tests): the first <c>$fanOut</c> in this order, which is the format's order
    /// wherever the keys of the records tied with the last one kept are below U+D800 (Cypher compares strings by code point,
    /// which agrees with UTF-16 code units there), and every tied record when one of those keys is not (<c>$wide</c>). Records
    /// are ranked by reference; their properties are read only for the rows returned. Without question words nothing is folded.
    /// </remarks>
    private static string Ranked(string node) =>
        "WITH fid, " + node + ", degree, CASE WHEN size($words) = 0 THEN '' ELSE "
        + "reduce(text = '', k IN $text | text + ' ' + coalesce(toString(" + node + "[k]), '')) END AS raw "
        + "WITH fid, " + node + ", degree, CASE WHEN raw = '' THEN '' ELSE reduce(text = raw, f IN $fold | replace(text, f[0], f[1])) END AS text "
        + "WITH fid, " + node + ", degree, size([w IN $words WHERE text CONTAINS w]) AS hits "
        + "ORDER BY hits DESC, degree ASC, " + node + ".key, " + node + ".id "
        + "WITH fid, collect({node: " + node + ", degree: degree, hits: hits}) AS ranked "
        + "WITH fid, ranked, ranked[$fanOut - 1] AS edge "
        + "WITH fid, ranked, edge, edge IS NOT NULL AND any(r IN ranked WHERE r.hits = edge.hits AND r.degree = edge.degree AND r.node.key =~ $wide) AS wide "
        + "UNWIND range(0, size(ranked) - 1) AS i WITH fid, ranked[i] AS r, i, edge, wide "
        + "WHERE i < $fanOut OR (wide AND r.hits = edge.hits AND r.degree = edge.degree) "
        + "RETURN fid, properties(r.node) AS row, r.degree AS degree, r.hits AS hits";

    /// <summary>The pattern of a key that holds a character at or above U+D800, where code-point and UTF-16 orders can differ.</summary>
    internal const string WideKey = @"(?s).*[\x{D800}-\x{10FFFF}].*";
}
