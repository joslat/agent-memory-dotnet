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

    /// <summary>Exact matches, compared without case: <c>$values</c> are lower-cased by the caller.</summary>
    internal static string MatchExact(string label, string property) =>
        "MATCH (n:`" + label + "`) WHERE n.invalidated_at IS NULL AND " + In("n") + " AND toLower(n.`" + property + "`) IN $values "
        + "RETURN properties(n) AS row ORDER BY n.id LIMIT $take";

    /// <summary>Every live record whose property is set; the walk matches prefixes, globs and names itself.</summary>
    internal static string MatchAny(string label, string property) =>
        "MATCH (n:`" + label + "`) WHERE n.invalidated_at IS NULL AND " + In("n") + " AND n.`" + property + "` IS NOT NULL "
        + "RETURN properties(n) AS row ORDER BY n.id LIMIT $take";

    /// <summary>
    /// Out: each live source's link targets, as their live versions (a link may point at an older version of its target; the
    /// live one shares its key identity, <c>key_id</c>). At most <c>$fanOut</c> per source, ranked (see <see cref="Ranked"/>);
    /// a target's degree is how many live sources in scope link to it, counted up to <c>$scan</c>.
    /// </summary>
    /// <remarks>
    /// <b>At most <c>$scan</c> targets per source are read at all</b> (red-team M3, PACK-review 1.4): the limit sits inside the
    /// read, so the store stops expanding a hub there instead of reading and sorting every link first; when a record has more
    /// links than that, which ones are read is the store's order. The degree count stops at <c>$scan</c> too.
    /// </remarks>
    internal static string Out(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (s:`" + source + "` {id:fid}) WHERE s.invalidated_at IS NULL "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->(old:`" + target + "`) "
        + "MATCH (t:`" + target + "` {key_id:old.key_id}) WHERE t.invalidated_at IS NULL AND " + In("t") + " "
        + "RETURN DISTINCT t LIMIT $scan } "
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
        + "RETURN DISTINCT s LIMIT $scan } "
        + "CALL { WITH s MATCH (s)-[:`" + relation + "`]->() RETURN count(*) AS degree } "
        + Ranked("s");

    /// <summary>
    /// Per starting record, the reached records whose text (the <c>$text</c> properties, lower case) holds most of the question's
    /// <c>$words</c> first, then the most specific (lowest degree), then by id; at most <c>$fanOut</c> (CMP-4).
    /// </summary>
    private static string Ranked(string node) =>
        "WITH fid, " + node + ", degree, reduce(text = '', k IN $text | text + ' ' + toLower(coalesce(toString(" + node + "[k]), ''))) AS text "
        + "WITH fid, " + node + ", degree, size([w IN $words WHERE text CONTAINS w]) AS hits "
        + "ORDER BY hits DESC, degree ASC, " + node + ".id "
        + "WITH fid, collect({row: properties(" + node + "), degree: degree})[..$fanOut] AS rows "
        + "UNWIND rows AS r RETURN fid, r.row AS row, r.degree AS degree";
}
