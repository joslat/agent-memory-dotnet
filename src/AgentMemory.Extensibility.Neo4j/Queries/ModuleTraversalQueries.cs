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
    /// live one shares its key and owner bucket). At most <c>$fanOut</c> per source, in id order.
    /// </summary>
    internal static string Out(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (s:`" + source + "` {id:fid}) WHERE s.invalidated_at IS NULL "
        + "MATCH (s)-[:`" + relation + "`]->(old:`" + target + "`) "
        + "MATCH (t:`" + target + "` {key:old.key}) WHERE t.invalidated_at IS NULL AND coalesce(t.owner_id,'')=coalesce(old.owner_id,'') AND " + In("t") + " "
        + "WITH fid, t ORDER BY t.id WITH fid, collect(DISTINCT properties(t))[..$fanOut] AS rows UNWIND rows AS row RETURN fid, row";

    /// <summary>
    /// In: the live sources whose link points at any version of each live target, so links written before the target changed
    /// still count. At most <c>$fanOut</c> per target, in id order.
    /// </summary>
    internal static string In(string source, string relation, string target) =>
        "UNWIND $ids AS fid MATCH (x:`" + target + "` {id:fid}) WHERE x.invalidated_at IS NULL "
        + "MATCH (v:`" + target + "` {key:x.key}) WHERE coalesce(v.owner_id,'')=coalesce(x.owner_id,'') "
        + "MATCH (s:`" + source + "`)-[:`" + relation + "`]->(v) WHERE s.invalidated_at IS NULL AND " + In("s") + " "
        + "WITH fid, s ORDER BY s.id WITH fid, collect(DISTINCT properties(s))[..$fanOut] AS rows UNWIND rows AS row RETURN fid, row";
}
