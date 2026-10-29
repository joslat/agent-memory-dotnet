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
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->(old:`" + target + "`) "
        + "MATCH (t:`" + target + "` {key_id:old.key_id}) WHERE t.invalidated_at IS NULL AND " + In("t") + " "
        + "RETURN DISTINCT t } "
        + "CALL { WITH t MATCH (:`" + target + "` {key_id:t.key_id})<-[:`" + relation + "`]-(o:`" + source + "`) "
        + "WHERE o.invalidated_at IS NULL AND " + In("o") + " WITH DISTINCT o LIMIT $scan RETURN count(o) AS degree } "
        + Ranked("t");

    /// <summary>
    /// In: the live sources in scope whose link points at any version of each live target, so links written before the target
    /// changed still count (every version shares its key identity, <c>key_id</c>, which is indexed), ranked (see
    /// <see cref="Ranked"/>); a source's degree is how many live targets in scope it links to, counted up to <c>$scan</c>, as the
    /// in-memory store counts it (review 3: links to closed or other owners' targets counted here).
    /// </summary>
    internal static string In(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (x:`" + target + "` {id:fid}) WHERE x.invalidated_at IS NULL "
        + "CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) "
        + "MATCH (s:`" + source + "`)-[:`" + relation + "`]->(v) WHERE s.invalidated_at IS NULL AND " + In("s") + " "
        + "RETURN DISTINCT s } "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->(old:`" + target + "`) MATCH (t:`" + target + "` {key_id:old.key_id}) "
        + "WHERE t.invalidated_at IS NULL AND " + In("t") + " WITH DISTINCT t LIMIT $scan RETURN count(t) AS degree } "
        + Ranked("s");

    /// <summary>
    /// Per starting record, the reached records ranked by how many of the question's <c>$words</c> their text (the <c>$text</c>
    /// properties, ASCII-folded with <c>$fold</c>) holds, then the lowest degree; every record that ranks above or alongside the
    /// <c>$fanOut</c>-th is returned with its count, and the caller takes the first <c>$fanOut</c> in the format's order (review 3:
    /// the cut inside Cypher depended on the store's string order, and its folding counted words the walk did not).
    /// </summary>
    private static string Ranked(string node) =>
        "WITH fid, " + node + ", degree, reduce(text = '', k IN $text | text + ' ' + coalesce(toString(" + node + "[k]), '')) AS raw "
        + "WITH fid, " + node + ", degree, reduce(text = raw, f IN $fold | replace(text, f[0], f[1])) AS text "
        + "WITH fid, " + node + ", degree, size([w IN $words WHERE text CONTAINS w]) AS hits "
        + "ORDER BY hits DESC, degree ASC "
        + "WITH fid, collect({row: properties(" + node + "), degree: degree, hits: hits}) AS ranked "
        + "WITH fid, ranked, ranked[$fanOut - 1] AS edge "
        + "UNWIND ranked AS r WITH fid, r, edge WHERE edge IS NULL OR r.hits > edge.hits OR (r.hits = edge.hits AND r.degree <= edge.degree) "
        + "RETURN fid, r.row AS row, r.degree AS degree, r.hits AS hits";
}
