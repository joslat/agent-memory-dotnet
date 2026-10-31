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

    // A value as the store's filter reads it: lower case, I and J first made i and j, so that no locale changes them (review 4:
    // under a Turkish or Azerbaijani locale toLower makes I a dotless i, under a Lithuanian one I and J before an accent gain a
    // dot), and the other ASCII letters fold the same everywhere. Both sides go through it, so two values the walk's ASCII
    // folding makes equal always meet, whatever else the locale folds: what differs between them is only ASCII case.
    private static string Lower(string value) => "toLower(replace(replace(" + value + ", 'I', 'i'), 'J', 'j'))";

    // A stored value as the store's filter reads it: Lower, with '\' as '/' (Cypher's '\\' is one backslash).
    private static string Slashed(string value) => "replace(" + Lower(value) + @", '\\', '/')";

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
        // The anchors lowered once a statement, and the stored value lowered and slashed once a record (review 7, W3: both were
        // recomputed for every anchor folder on every record).
        var filter = match switch
        {
            Modules.ModuleAnchorMatch.Exact => "ANY(v IN values WHERE s CONTAINS v)",
            Modules.ModuleAnchorMatch.Prefix => "ANY(f IN folders WHERE s CONTAINS f)",
            Modules.ModuleAnchorMatch.Glob => "(value CONTAINS '*' OR value CONTAINS '?' OR ANY(f IN folders WHERE s CONTAINS f))",
            _ => "ANY(x IN $names WHERE value CONTAINS x)",
        };
        var slashed = match == Modules.ModuleAnchorMatch.Name ? "" : ", " + Slashed(p) + " AS s";
        // Narrow rows (review 6, M2: every candidate came back whole): the id, key and value are what matching needs.
        return "WITH [v IN $values | " + Lower("v") + "] AS values, [f IN $folders | " + Lower("f") + "] AS folders "
            + "MATCH (n:`" + label + "`) WHERE n.invalidated_at IS NULL AND " + LiveAt("n") + " AND " + In("n") + " AND " + p + " IS NOT NULL "
            + "WITH n, values, folders, " + p + " AS value" + slashed + " WHERE " + filter + " "
            + "RETURN n.id AS id, n.key AS key, value";
    }
    /// <summary>The live records with these ids, whole: the matches the walk keeps, read once they are chosen.</summary>
    internal static string ByIds(string label) =>
        "UNWIND $ids AS rid MATCH (n:`" + label + "` {id:rid}) WHERE n.invalidated_at IS NULL AND " + LiveAt("n") + " RETURN properties(n) AS row";

    /// <summary>
    /// A record live in world time at <c>$validAt</c> (feature 07, R-HOST-058): the Cypher twin of ModuleRecordLife.IsLive, held to
    /// it by the store-agreement tests, applied before the store's own cuts.
    /// </summary>
    internal static string LiveAt(string n) =>
        "(" + n + ".valid_from IS NULL OR " + n + ".valid_from <= datetime($validAt)) AND (" + n + ".valid_to IS NULL OR " + n + ".valid_to > datetime($validAt)) "
        + "AND (" + n + ".expires_at IS NULL OR " + n + ".expires_at > datetime($validAt))";

    // The link count the limit is checked against, as the in-memory store counts it (review 2, 3): the store's degree first,
    // read without the links; only past the limit the distinct live records in scope at the other end, at most $over of them.
    // Under the limit, the degree itself (an upper bound of the live count, so the record is within the limit either way).
    // Each branch gives a row only when its condition holds: the exact count runs in a subquery of its own, past the filter, since
    // an ungrouped count over no rows is still one row, of 0 (the differential guard: every record under the limit came back
    // twice, and the fan-out cut kept half as many neighbours).
    private static string OutLinks(string relation, string target) =>
        "CALL { WITH s WITH s, COUNT { (s)-[:`" + relation + "`]->() } AS raw "
        + "CALL { WITH s, raw WITH s, raw WHERE raw > $maxLinks "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->(old:`" + target + "`) MATCH (t:`" + target + "` {key_id:old.key_id}) "
        + "WHERE t.invalidated_at IS NULL AND " + In("t") + " WITH DISTINCT t LIMIT $over RETURN count(t) AS links } RETURN links "
        + "UNION ALL WITH s, raw WITH raw WHERE raw <= $maxLinks RETURN raw AS links } RETURN links }";

    private static string InLinks(string source, string relation, string target) =>
        // Each version bound first, then its degree counted with an open end, which the store reads without the links.
        "CALL { WITH x CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) RETURN sum(COUNT { (v)<-[:`" + relation + "`]-() }) AS raw } "
        + "CALL { WITH x, raw WITH x, raw WHERE raw > $maxLinks "
        + "CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) MATCH (s:`" + source + "`)-[:`" + relation + "`]->(v) "
        + "WHERE s.invalidated_at IS NULL AND " + In("s") + " WITH DISTINCT s LIMIT $over RETURN count(s) AS links } RETURN links "
        + "UNION ALL WITH x, raw WITH raw WHERE raw <= $maxLinks RETURN raw AS links } RETURN links }";

    /// <summary>
    /// Out: each live source's link targets, as their live versions (a link may point at an older version of its target; the
    /// live one shares its key identity, <c>key_id</c>), ranked (see <see cref="Ranked"/>); a target's degree is how many live
    /// sources in scope link to it, counted up to <c>$scan</c>.
    /// </summary>
    /// <remarks>
    /// <b>Every target of a source within the link limit is ranked</b> (review 2): a record past <c>$maxLinks</c> is left out
    /// in the same statement (<see cref="OutLinks"/>, review 4: a statement of its own before), so the cost stays bounded, and the
    /// targets ranked are all of them. Every row carries the starting record's key and link count; one past the limit returns
    /// that row alone, with no record.
    /// </remarks>
    internal static string Out(string source, string relation, string target) =>
        // The starting record in scope too (review 6, L6: another owner's id gave its key in the hub problem).
        "UNWIND $ids AS fid MATCH (s:`" + source + "` {id:fid}) WHERE s.invalidated_at IS NULL AND " + In("s") + " " + OutLinks(relation, target) + " "
        + "CALL { WITH fid, s, links WITH fid, s, links WHERE links <= $maxLinks "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->(old:`" + target + "`) " + Live("old", "t", target) + " "
        + "WITH t WHERE " + In("t") + " AND " + LiveAt("t") + " RETURN DISTINCT t } "
        + "CALL { WITH t MATCH (:`" + target + "` {key_id:t.key_id})<-[:`" + relation + "`]-(o:`" + source + "`) "
        + "WHERE o.invalidated_at IS NULL AND " + In("o") + " WITH DISTINCT o LIMIT $scan RETURN count(o) AS degree } "
        + Ranked("t") + " "
        + "UNION ALL WITH fid, s, links WITH links WHERE links > $maxLinks RETURN null AS id, null AS rkey, null AS degree, null AS hits } "
        + "RETURN fid, s.key AS key, links, id, rkey, degree, hits";

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
    /// <see cref="Ranked"/>); a source's degree is how many live keys in scope its links of the relation lead to, counted up to
    /// <c>$scan</c>, as the in-memory store counts a record's links to the records it holds (pack format v1.2 §6.1).
    /// </summary>
    /// <remarks>
    /// Review 4, H1: counting the record's own links did not agree with the in-memory store across modules. A work item still
    /// links to a code-map file a later import removed; memory, which holds only what is mounted, does not count it. A version
    /// links each key once, so the links to live keys are the distinct keys: one <c>COUNT</c>, no subquery, and a link to a live
    /// version needs no key lookup; only a link to a closed version asks whether its key still has a current version. (Review 3
    /// first made this exact with a subquery per neighbour resolving every target's live version and de-duplicating: the walk's
    /// p95 rose from 34 to 54 ms on this repository's code map.)
    /// </remarks>
    internal static string In(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (x:`" + target + "` {id:fid}) WHERE x.invalidated_at IS NULL AND " + In("x") + " " + InLinks(source, relation, target) + " "
        + "CALL { WITH fid, x, links WITH fid, x, links WHERE links <= $maxLinks "
        + "CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) "
        + "MATCH (s:`" + source + "`)-[:`" + relation + "`]->(v) WHERE s.invalidated_at IS NULL AND " + LiveAt("s") + " AND " + In("s") + " "
        + "RETURN DISTINCT s } "
        + "WITH fid, s, COUNT { (s)-[:`" + relation + "`]->(v) WHERE " + In("v") + " AND (v.invalidated_at IS NULL "
        + "OR EXISTS { MATCH (k:ModuleKey {id:v.key_id}) WHERE k.current_id IS NOT NULL }) } AS own "
        + "WITH fid, s, CASE WHEN own > $scan THEN $scan ELSE own END AS degree "
        + Ranked("s") + " "
        + "UNION ALL WITH fid, x, links WITH links WHERE links > $maxLinks RETURN null AS id, null AS rkey, null AS degree, null AS hits } "
        + "RETURN fid, x.key AS key, links, id, rkey, degree, hits";

    /// <summary>
    /// Per starting record, the reached records ranked by how many of the question's <c>$words</c> their text (the <c>$text</c>
    /// properties, ASCII-folded with <c>$fold</c>) holds, then the lowest degree, then key and version id; the caller takes the
    /// first <c>$fanOut</c> in the format's order (review 3: the cut inside Cypher depended on the store's string order, and its
    /// folding counted words the walk did not).
    /// </summary>
    /// <remarks>
    /// What is returned is a superset of what the cut keeps, and small (review 3, performance: every tie with its properties was
    /// returned, hundreds of rows for a hub of alike tests): the first <c>$fanOut</c> in this order, which is the format's order
    /// (Cypher compares strings by code point, as the format does since feature 02), and every tied record when one of those
    /// keys is at or above U+D800 (<c>$wide</c>: kept from when the format ordered by UTF-16 unit; a superset, so harmless). Records
    /// are ranked by reference, and each comes back narrow, its id, key, degree and hits (review 8, W2: every tied record came back
    /// whole when one tied key was wide); the caller cuts, then reads whole only the records it keeps (<see cref="ByIds"/>).
    /// Without question words nothing is folded.
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
        + "RETURN r.node.id AS id, r.node.key AS rkey, r.degree AS degree, r.hits AS hits";

    /// <summary>The pattern of a key that holds a character at or above U+D800, where code-point and UTF-16 orders can differ.</summary>
    internal const string WideKey = @"(?s).*[\x{D800}-\x{10FFFF}].*";
}
