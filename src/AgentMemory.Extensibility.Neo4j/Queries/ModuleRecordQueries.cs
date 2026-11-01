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
        // A changed life is a changed record (feature 07): its instants compare as its properties do.
        + string.Concat(LifeProperties.Select(p => " AND coalesce(n." + p.Name + "=datetime($" + p.Parameter + "), n." + p.Name + " IS NULL AND $" + p.Parameter + " IS NULL)"))
        // A changed confidence is a changed record too (feature 09).
        + " AND coalesce(n.confidence_micro=$confidence, n.confidence_micro IS NULL AND $confidence IS NULL)"
        // And a changed usage policy (feature 10): the record names the policy that governs it.
        + " AND coalesce(n.source_policy=$policy, n.source_policy IS NULL AND $policy IS NULL)"
        + " AS equal, coalesce(coalesce(n.invalidated_at,n.created_at) > datetime($now),false) AS newer";

    // A record's life in world time (feature 07): stored property, query parameter.
    internal static readonly (string Name, string Parameter)[] LifeProperties =
        [("valid_from", "validFrom"), ("valid_to", "validTo"), ("expires_at", "expiresAt"), ("review_by", "reviewBy")];
    internal static string Create(ModuleType type) => "CREATE (n:`" + type.Name + "`) SET n=$properties "
        + "SET n.id=$id, n.key_id=$keyId, n.key=$key, n.owner_id=$owner, n.created_at=datetime($now), "
        + "n.source_session_id=$session, n.source_message_ids=$sources, n.source_excerpt=$excerpt, "
        + "n.source_pack=$pack, n.source_pack_version=$packVersion, n.source_pack_digest=$packDigest, n.source_pack_file=$packFile, n.source_pack_line=$packLine, "
        + "n.source_carrier=$carrier, n.source_carrier_version=$carrierVersion, n.source_carrier_digest=$carrierDigest, n.source_carrier_signer=$carrierSigner, "
        + "n.source_policy=$policy, "
        + string.Join(", ", LifeProperties.Select(p => "n." + p.Name + "=CASE WHEN $" + p.Parameter + " IS NULL THEN null ELSE datetime($" + p.Parameter + ") END")) + ", "
        + "n.confidence_micro=$confidence "
        + string.Join(" ", type.Properties.Where(p => p.Value.Type is "date" or "datetime").Select(p => "SET n.`" + p.Key + "`=" + Value(p.Key, p.Value.Type)))
        + " RETURN n.id AS id";
    // A knowledge pack's live records (Extensibility 0.13 §K3): what an import may close, and what its state reports.
    internal static string PackLive(string label) => "MATCH (n:`" + label + "`) WHERE n.source_pack=$pack AND n.invalidated_at IS NULL "
        + "RETURN n.key AS key, n.source_pack_version AS version";
    // A snapshot's live records with their properties and links by target key (CMP-2a: one read instead of one per record).
    internal static string SnapshotState(string label) => "MATCH (n:`" + label + "`) WHERE n.source_pack=$pack AND n.invalidated_at IS NULL "
        + "OPTIONAL MATCH (n)-[r]->(t) WHERE type(r) IN $relations "
        + "RETURN n.key AS key, properties(n) AS row, collect(CASE WHEN r IS NULL THEN null ELSE [type(r), t.key] END) AS links";
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
    /// <summary>
    /// Links live records by key identity (CMP-2c): a snapshot's link to a record written later in the same import, added to the
    /// version that import wrote. Returns how many of <c>$links</c> it linked.
    /// </summary>
    internal static string LinkLive(string source, string relation, string target) =>
        "UNWIND $links AS l MATCH (n:`" + source + "` {key_id:l.source}) WHERE n.invalidated_at IS NULL AND n.superseded_by IS NULL "
        + "MATCH (t:`" + target + "` {key_id:l.target}) WHERE t.invalidated_at IS NULL AND t.merged_into IS NULL AND t.superseded_by IS NULL "
        + "MERGE (n)-[:`" + relation + "`]->(t) RETURN count(*) AS linked";
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
