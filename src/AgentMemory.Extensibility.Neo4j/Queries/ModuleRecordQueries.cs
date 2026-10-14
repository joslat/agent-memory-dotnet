using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Neo4j.Queries;

[Experimental("AMEXT001")]
internal static class ModuleRecordQueries
{
    internal const string Sources = """
        MATCH (c:Conversation)-[:HAS_MESSAGE]->(m:Message)
        WHERE m.id IN $ids AND c.session_id=$session AND m.session_id=$session AND m.conversation_id=c.id
          AND (($owner IS NULL AND c.user_id IS NULL) OR c.user_id=$owner)
          AND m.invalidated_at IS NULL
        RETURN DISTINCT m.id AS id, m.role AS role, m.content AS text
        """;
    // Force a node write lock before reading the version/current pair; a no-op assignment can
    // expose stale property reads under contention. Remove the transient marker in the same query.
    // The unique id constraint also serializes the initial MERGE.
    internal const string LockKey = """
        MERGE (k:ModuleKey {id:$keyId})
        ON CREATE SET k.module_id=$module, k.type=$type, k.owner_id=$owner, k.key=$key, k.version=0
        SET k.lock_token=true
        REMOVE k.lock_token
        RETURN properties(k) AS row
        """;
    internal const string SetCurrent = "MATCH (k:ModuleKey {id:$keyId}) SET k.version=$version, k.current_id=$id RETURN k.id AS id";
    internal static string Current(ModuleType type) => "MATCH (n:`" + type.Name + "` {id:$id}) RETURN properties(n) AS row, n.invalidated_at IS NULL AS live, "
        + (type.Properties.Count == 0 ? "true" : string.Join(" AND ", type.Properties.Select(p => "coalesce(n.`" + p.Key + "`=" + Value(p.Key, p.Value.Type)
            + ", n.`" + p.Key + "` IS NULL AND $properties.`" + p.Key + "` IS NULL)")))
        + " AS equal, coalesce(coalesce(n.invalidated_at,n.created_at) > datetime($now),false) AS newer";
    internal static string Create(ModuleType type) => "CREATE (n:`" + type.Name + "`) SET n=$properties "
        + "SET n.id=$id, n.key_id=$keyId, n.key=$key, n.owner_id=$owner, n.created_at=datetime($now), "
        + "n.source_session_id=$session, n.source_message_ids=$sources, n.source_excerpt=$excerpt "
        + string.Join(" ", type.Properties.Where(p => p.Value.Type is "date" or "datetime").Select(p => "SET n.`" + p.Key + "`=" + Value(p.Key, p.Value.Type)))
        + " RETURN n.id AS id";
    internal static string Invalidate(string label) => "MATCH (n:`" + label + "` {id:$id}) SET n.invalidated_at=datetime($now) "
        + "WITH n MATCH (k:ModuleKey {id:$keyId}) SET k.current_id=null RETURN n.id AS id";
    internal static string Supersede(string label) => "MATCH (old:`" + label + "` {id:$previous}), (n:`" + label + "` {id:$id}) "
        + "SET old.invalidated_at=coalesce(old.invalidated_at,datetime($now)), old.superseded_by=$id "
        + "MERGE (old)-[:SUPERSEDED_BY]->(n) RETURN n.id AS id";
    internal static string Links(string label) => "MATCH (n:`" + label + "` {id:$id})-[r]->(target) WHERE type(r) IN $relations "
        + "RETURN type(r) AS relation, target.id AS target";
    internal static string Target(string label, bool name, bool key) => "MATCH (n:`" + label + "`) WHERE "
        + (name ? "(n.name=$target OR $target IN coalesce(n.aliases,[]))" : key ? "n.key=$target AND n.invalidated_at IS NULL" : "n.id=$target")
        + " AND (n.owner_id IS NULL OR ($owner IS NOT NULL AND n.owner_id=$owner)) RETURN properties(n) AS row LIMIT 65";
    internal static string Link(string source, string relation, string target) => "MATCH (n:`" + source + "` {id:$id}), (t:`" + target + "` {id:$target}) "
        + "WHERE t.invalidated_at IS NULL AND t.merged_into IS NULL AND t.superseded_by IS NULL "
        + "AND (t.owner_id IS NULL OR ($owner IS NOT NULL AND t.owner_id=$owner)) "
        + "MERGE (n)-[:`" + relation + "`]->(t) RETURN t.id AS id";
    private static string Value(string name, string type) => type switch
    {
        "date" => "date($properties.`" + name + "`)",
        "datetime" => "datetime($properties.`" + name + "`)",
        _ => "$properties.`" + name + "`"
    };
}
