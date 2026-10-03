namespace AgentMemory.Neo4j.Queries;

/// <summary>G3 (PLAN 40.47): erase, export and import an owner's data.</summary>
public static class OwnerDataQueries
{
    /// <summary>A batch of the owner's stamped nodes, deleted with their edges, counted by label.</summary>
    public const string EraseOwnedBatch = @"
            MATCH (n) WHERE n.owner_id = $ownerId
            WITH n LIMIT 2000
            WITH n, head(labels(n)) AS label
            DETACH DELETE n
            RETURN label, count(*) AS deleted";

    /// <summary>A batch of the messages in the owner's conversations (messages carry no owner of their own).</summary>
    public const string EraseMessagesBatch = @"
            MATCH (:Conversation {user_id: $ownerId})-[:HAS_MESSAGE]->(m:Message)
            WITH DISTINCT m LIMIT 2000
            DETACH DELETE m
            RETURN 'Message' AS label, count(*) AS deleted";

    /// <summary>A batch of the owner's conversations.</summary>
    public const string EraseConversationsBatch = @"
            MATCH (c:Conversation {user_id: $ownerId})
            WITH c LIMIT 2000
            DETACH DELETE c
            RETURN 'Conversation' AS label, count(*) AS deleted";

    /// <summary>
    /// The owner's live entities. A merged-away or forgotten entity is left out: imported, it would come back live beside
    /// the one it was merged into.
    /// </summary>
    public const string ExportEntities = @"
            MATCH (n:Entity) WHERE n.owner_id = $ownerId AND n.invalidated_at IS NULL AND n.merged_into IS NULL
            RETURN n ORDER BY n.created_at, n.id";

    /// <summary>The owner's facts, live and closed, oldest first.</summary>
    public const string ExportFacts = "MATCH (n:Fact) WHERE n.owner_id = $ownerId RETURN n ORDER BY n.created_at, n.id";

    /// <summary>The owner's preferences, live and closed, oldest first.</summary>
    public const string ExportPreferences = "MATCH (n:Preference) WHERE n.owner_id = $ownerId RETURN n ORDER BY n.created_at, n.id";

    /// <summary>The owner's relationship edges between entities that are exported or shared (see <see cref="ExportAbout"/>).</summary>
    public const string ExportRelationships = @"
            MATCH (a:Entity)-[r:RELATED_TO]->(b:Entity) WHERE r.owner_id = $ownerId
              AND ((a.owner_id = $ownerId AND a.invalidated_at IS NULL AND a.merged_into IS NULL) OR a.owner_id IS NULL)
              AND ((b.owner_id = $ownerId AND b.invalidated_at IS NULL AND b.merged_into IS NULL) OR b.owner_id IS NULL)
            RETURN r";

    /// <summary>Which of the owner's facts replaced which.</summary>
    public const string ExportSupersessions = @"
            MATCH (l:Fact)-[:SUPERSEDED_BY]->(w:Fact) WHERE l.owner_id = $ownerId
            RETURN l.id AS from, w.id AS to";

    /// <summary>
    /// Which entities the owner's facts are about: the exported ones, or shared ones. Nothing else: an import keeps a link
    /// to an entity it did not write by its id, which is right only for a shared entity (in the same store, the id of a
    /// merged-away entity of the owner would join two owners).
    /// </summary>
    public const string ExportAbout = @"
            MATCH (f:Fact)-[:ABOUT]->(e:Entity) WHERE f.owner_id = $ownerId
              AND ((e.owner_id = $ownerId AND e.invalidated_at IS NULL AND e.merged_into IS NULL) OR e.owner_id IS NULL)
            RETURN f.id AS from, e.id AS to";

    /// <summary>An imported fact's closing, as it was.</summary>
    public const string RestoreFactClosing = @"
            MATCH (f:Fact {id: $id})
            SET f.invalidated_at = datetime($invalidatedAt), f.invalidated_reason = $reason";

    /// <summary>An imported preference's closing, as it was.</summary>
    public const string RestorePreferenceClosing = @"
            MATCH (p:Preference {id: $id})
            SET p.invalidated_at = datetime($invalidatedAt)";

    /// <summary>An imported supersession.</summary>
    public const string LinkSupersession = @"
            MATCH (l:Fact {id: $from}), (w:Fact {id: $to})
            MERGE (l)-[:SUPERSEDED_BY]->(w)";
}
