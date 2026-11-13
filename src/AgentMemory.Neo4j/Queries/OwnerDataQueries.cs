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
    // ILC-18 interim: which exported observation a confirmed identity decision resolved to which identity (read only).
    public const string ExportIdentityResolutions = """
        MATCH (d:IdentityDecision {owner_scope: 'owned:' + $ownerId})-[:RESOLVES_TO]->(t:Entity)
        MATCH (o:Entity {identity_observation_key: d.identity_observation_key})
        WHERE o.owner_id = $ownerId AND o.id <> t.id AND o.merged_into IS NULL AND o.invalidated_at IS NULL
        RETURN DISTINCT o.id AS from, t.id AS to ORDER BY from, to
        """;

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

    /// <summary>
    /// ILC-30: the identity records a full round trip carries, each with its export and import. The scope lock is left
    /// out: it is infrastructure, made again by the next identity write in the new owner's scope.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string Export, string Import)> IdentityRecords =
        new Dictionary<string, (string Export, string Import)>(StringComparer.Ordinal)
        {
            ["IdentityDecision"] = (
                "MATCH (n:IdentityDecision) WHERE n.owner_id = $ownerId RETURN n.id AS key, properties(n) AS props ORDER BY n.id",
                "UNWIND $rows AS row CREATE (n:IdentityDecision) SET n = row RETURN count(n) AS written"),
            ["IdentityDecisionRevision"] = (
                "MATCH (n:IdentityDecisionRevision) WHERE n.owner_id = $ownerId RETURN n.id AS key, properties(n) AS props ORDER BY n.id",
                "UNWIND $rows AS row CREATE (n:IdentityDecisionRevision) SET n = row RETURN count(n) AS written"),
            ["IdentityFactBinding"] = (
                "MATCH (n:IdentityFactBinding) WHERE n.owner_id = $ownerId RETURN n.id AS key, properties(n) AS props ORDER BY n.id",
                "UNWIND $rows AS row CREATE (n:IdentityFactBinding) SET n = row RETURN count(n) AS written"),
            ["IdentityEvidenceAssertion"] = (
                "MATCH (n:IdentityEvidenceAssertion) WHERE n.owner_id = $ownerId RETURN n.id AS key, properties(n) AS props ORDER BY n.id",
                "UNWIND $rows AS row CREATE (n:IdentityEvidenceAssertion) SET n = row RETURN count(n) AS written"),
            ["IdentityRelationshipObservation"] = (
                "MATCH (n:IdentityRelationshipObservation) WHERE n.owner_id = $ownerId RETURN n.id AS key, properties(n) AS props ORDER BY n.id",
                "UNWIND $rows AS row CREATE (n:IdentityRelationshipObservation) SET n = row RETURN count(n) AS written"),
        };

    /// <summary>ILC-30: the identity properties of the owner's exported entities.</summary>
    public const string ExportEntityIdentity = @"
            MATCH (e:Entity) WHERE e.owner_id = $ownerId AND e.invalidated_at IS NULL AND e.merged_into IS NULL
              AND (e.identity_observation_key IS NOT NULL OR e.identity_evidence_version IS NOT NULL OR e.identity_calendar_key IS NOT NULL)
            RETURN e.id AS key, {identity_observation_key: e.identity_observation_key,
                                 identity_evidence_version: e.identity_evidence_version,
                                 identity_calendar_key: e.identity_calendar_key} AS props
            ORDER BY e.id";

    /// <summary>ILC-30: an imported entity's identity properties.</summary>
    public const string ImportEntityIdentity = @"
            UNWIND $rows AS row
            MATCH (e:Entity {id: row.key})
            SET e += row.props
            RETURN count(e) AS written";

    /// <summary>
    /// ILC-30: every link identity storage makes (IdentityEvidenceSchemaExtension's relationship types), by the kinds of
    /// record it joins, with its export (the owner's side) and import. An end that is not imported (a shared entity) is
    /// matched as it is, or the link is not made.
    /// </summary>
    internal static readonly IReadOnlyList<(string FromLabel, string Type, string ToLabel, string Export, string Import)> IdentityLinks =
    [
        ("Fact", "HAS_IDENTITY_BINDING", "IdentityFactBinding",
            "MATCH (a:Fact)-[r:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding) WHERE b.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:Fact {id: row.from}) MATCH (b:IdentityFactBinding {id: row.to}) CREATE (a)-[r:HAS_IDENTITY_BINDING]->(b) SET r = row.props RETURN count(r) AS written"),
        ("IdentityFactBinding", "DECIDED_BY", "IdentityDecision",
            "MATCH (a:IdentityFactBinding)-[r:DECIDED_BY]->(b:IdentityDecision) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityFactBinding {id: row.from}) MATCH (b:IdentityDecision {id: row.to}) CREATE (a)-[r:DECIDED_BY]->(b) SET r = row.props RETURN count(r) AS written"),
        ("IdentityFactBinding", "RESOLVES_TO", "Entity",
            "MATCH (a:IdentityFactBinding)-[r:RESOLVES_TO]->(b:Entity) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityFactBinding {id: row.from}) MATCH (b:Entity {id: row.to}) CREATE (a)-[r:RESOLVES_TO]->(b) SET r = row.props RETURN count(r) AS written"),
        ("IdentityDecision", "RESOLVES_TO", "Entity",
            "MATCH (a:IdentityDecision)-[r:RESOLVES_TO]->(b:Entity) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityDecision {id: row.from}) MATCH (b:Entity {id: row.to}) CREATE (a)-[r:RESOLVES_TO]->(b) SET r = row.props RETURN count(r) AS written"),
        ("IdentityDecision", "HAS_IDENTITY_REVISION", "IdentityDecisionRevision",
            "MATCH (a:IdentityDecision)-[r:HAS_IDENTITY_REVISION]->(b:IdentityDecisionRevision) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityDecision {id: row.from}) MATCH (b:IdentityDecisionRevision {id: row.to}) CREATE (a)-[r:HAS_IDENTITY_REVISION]->(b) SET r = row.props RETURN count(r) AS written"),
        ("IdentityDecision", "POSSIBLE_SAME_AS", "Entity",
            "MATCH (a:IdentityDecision)-[r:POSSIBLE_SAME_AS]->(b:Entity) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityDecision {id: row.from}) MATCH (b:Entity {id: row.to}) CREATE (a)-[r:POSSIBLE_SAME_AS]->(b) SET r = row.props RETURN count(r) AS written"),
        ("Entity", "HAS_IDENTITY_EVIDENCE", "IdentityEvidenceAssertion",
            "MATCH (a:Entity)-[r:HAS_IDENTITY_EVIDENCE]->(b:IdentityEvidenceAssertion) WHERE b.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:Entity {id: row.from}) MATCH (b:IdentityEvidenceAssertion {id: row.to}) CREATE (a)-[r:HAS_IDENTITY_EVIDENCE]->(b) SET r = row.props RETURN count(r) AS written"),
        ("IdentityRelationshipObservation", "SOURCE_IDENTITY", "IdentityDecision",
            "MATCH (a:IdentityRelationshipObservation)-[r:SOURCE_IDENTITY]->(b:IdentityDecision) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityRelationshipObservation {id: row.from}) MATCH (b:IdentityDecision {id: row.to}) CREATE (a)-[r:SOURCE_IDENTITY]->(b) SET r = row.props RETURN count(r) AS written"),
        ("IdentityRelationshipObservation", "TARGET_IDENTITY", "IdentityDecision",
            "MATCH (a:IdentityRelationshipObservation)-[r:TARGET_IDENTITY]->(b:IdentityDecision) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityRelationshipObservation {id: row.from}) MATCH (b:IdentityDecision {id: row.to}) CREATE (a)-[r:TARGET_IDENTITY]->(b) SET r = row.props RETURN count(r) AS written"),
    ];

    /// <summary>An imported supersession.</summary>
    public const string LinkSupersession = @"
            MATCH (l:Fact {id: $from}), (w:Fact {id: $to})
            MERGE (l)-[:SUPERSEDED_BY]->(w)";
}
