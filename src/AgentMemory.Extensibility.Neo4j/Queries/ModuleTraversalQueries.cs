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

    // A stored value as the walk compares paths: lower case, with '\' as '/' (Cypher's '\\' is one backslash).
    private static string Slashed(string value) => "replace(toLower(" + value + @"), '\\', '/')";

    /// <summary>
    /// Every live record whose anchor property could match (review 2): a superset of the walk's own matches, never cut before
    /// matching. Exact: the value, as the walk normalizes it, ends with an anchor (<c>$values</c>); prefix: it holds one of an
    /// anchor's folders (<c>$folders</c>); glob: that, or it holds a wildcard; name: it holds an anchor's simple name
    /// (<c>$names</c>). The walk then matches each record exactly, by its own rules.
    /// </summary>
    internal static string MatchCandidates(string label, string property, Modules.ModuleAnchorMatch match)
    {
        var p = "n.`" + property + "`";
        var filter = match switch
        {
            Modules.ModuleAnchorMatch.Exact => "ANY(v IN $values WHERE " + Slashed("trim(" + p + ")") + " ENDS WITH v)",
            Modules.ModuleAnchorMatch.Prefix => "ANY(f IN $folders WHERE " + Slashed(p) + " CONTAINS f)",
            Modules.ModuleAnchorMatch.Glob => "(" + p + " CONTAINS '*' OR " + p + " CONTAINS '?' OR ANY(f IN $folders WHERE " + Slashed(p) + " CONTAINS f))",
            _ => "ANY(s IN $names WHERE " + p + " CONTAINS s)",
        };
        return "MATCH (n:`" + label + "`) WHERE n.invalidated_at IS NULL AND " + In("n") + " AND " + p + " IS NOT NULL AND " + filter + " "
            + "RETURN properties(n) AS row ORDER BY n.key, n.id LIMIT $take";
    }

    /// <summary>
    /// Out: the starting records with more links of the relation than <c>$maxLinks</c>, which are not expanded (review 2), with
    /// their counts. The count reads the store's degree, not the links.
    /// </summary>
    internal static string OutHubs(string source, string relation) =>
        "UNWIND $ids AS fid MATCH (s:`" + source + "` {id:fid}) WITH fid, s, COUNT { (s)-[:`" + relation + "`]->() } AS links "
        + "WHERE links > $maxLinks RETURN fid, s.key AS key, links";

    /// <summary>In: the starting records linked to, over every version, more than <c>$maxLinks</c> times (see <see cref="OutHubs"/>).</summary>
    internal static string InHubs(string relation, string target) =>
        "UNWIND $ids AS fid MATCH (x:`" + target + "` {id:fid}) "
        // Each version bound first, then its degree counted with an open end, which the store reads without the links.
        + "CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) RETURN sum(COUNT { (v)<-[:`" + relation + "`]-() }) AS links } "
        + "WITH fid, x, links WHERE links > $maxLinks RETURN fid, x.key AS key, links";

    /// <summary>
    /// Out: each live source's link targets, as their live versions (a link may point at an older version of its target; the
    /// live one shares its key identity, <c>key_id</c>). At most <c>$fanOut</c> per source, ranked (see <see cref="Ranked"/>);
    /// a target's degree is how many live sources in scope link to it, counted up to <c>$scan</c>.
    /// </summary>
    /// <remarks>
    /// <b>Every target of a source within the link limit is ranked</b> (review 2): a record past <c>$maxLinks</c> is left out
    /// before this read (<see cref="OutHubs"/>), so the cost stays bounded, and the targets ranked are all of them, not the first
    /// <c>$scan</c> in the store's order, which differed between stores. The degree count stops at <c>$scan</c>.
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
    /// In: the live sources whose link points at any version of each live target, so links written before the target changed
    /// still count (every version shares its key identity, <c>key_id</c>, which is indexed). At most <c>$fanOut</c> per target,
    /// ranked (see <see cref="Ranked"/>); a source's degree is how many links of the relation it has (a version links each target
    /// once), counted without reading the targets: under a type with hundreds of tests, reading each test's targets was most of
    /// a cold read. At most <c>$scan</c> sources per target are read at all, the limit inside the read (see <see cref="Out"/>).
    /// </summary>
    internal static string In(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (x:`" + target + "` {id:fid}) WHERE x.invalidated_at IS NULL "
        + "CALL { WITH x MATCH (v:`" + target + "` {key_id:x.key_id}) "
        + "MATCH (s:`" + source + "`)-[:`" + relation + "`]->(v) WHERE s.invalidated_at IS NULL AND " + In("s") + " "
        + "RETURN DISTINCT s } "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->() WITH 1 AS one LIMIT $scan RETURN count(*) AS degree } "
        + Ranked("s");

    /// <summary>
    /// Per starting record, the reached records whose text (the <c>$text</c> properties, lower case) holds most of the question's
    /// <c>$words</c> first, then the most specific (lowest degree), then by key and id, the format's order within one type; at most
    /// <c>$fanOut</c> (CMP-4, review 2).
    /// </summary>
    private static string Ranked(string node) =>
        "WITH fid, " + node + ", degree, reduce(text = '', k IN $text | text + ' ' + toLower(coalesce(toString(" + node + "[k]), ''))) AS text "
        + "WITH fid, " + node + ", degree, size([w IN $words WHERE text CONTAINS w]) AS hits "
        + "ORDER BY hits DESC, degree ASC, " + node + ".key, " + node + ".id "
        + "WITH fid, collect({row: properties(" + node + "), degree: degree})[..$fanOut] AS rows "
        + "UNWIND rows AS r RETURN fid, r.row AS row, r.degree AS degree";
}
