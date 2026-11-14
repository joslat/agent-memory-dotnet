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
            SET f.invalidated_at = datetime($invalidatedAt), f.invalidated_reason = $reason,
                // REV3-1: a replaced value leaves the open period, as superseding it does.
                f.period_key = CASE WHEN $closesPeriod THEN f.id ELSE f.period_key END";

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
                "UNWIND $rows AS row MERGE (n:IdentityEvidenceAssertion {id: row.id}) ON CREATE SET n = row RETURN count(n) AS written"),
            ["IdentityRelationshipObservation"] = (
                "MATCH (n:IdentityRelationshipObservation) WHERE n.owner_id = $ownerId RETURN n.id AS key, properties(n) AS props ORDER BY n.id",
                "UNWIND $rows AS row CREATE (n:IdentityRelationshipObservation) SET n = row RETURN count(n) AS written"),
        };

    /// <summary>ILC-30: the identity properties of the owner's entities, live and retired (import keeps those it creates).</summary>
    public const string ExportEntityIdentity = @"
            MATCH (e:Entity) WHERE e.owner_id = $ownerId
              AND (e.identity_observation_key IS NOT NULL OR e.identity_evidence_version IS NOT NULL OR e.identity_calendar_key IS NOT NULL)
            RETURN e.id AS key, {identity_observation_key: e.identity_observation_key,
                                 identity_evidence_version: e.identity_evidence_version,
                                 identity_calendar_key: e.identity_calendar_key} AS props
            ORDER BY e.id";

    /// <summary>
    /// ILC-30: an imported entity's identity properties, these three only and on the importing owner's entities only
    /// (PR260-F9: the file names nothing else).
    /// </summary>
    public const string ImportEntityIdentity = @"
            UNWIND $rows AS row
            MATCH (e:Entity {id: row.key}) WHERE e.owner_id = $ownerId
            SET e.identity_observation_key = row.props.identity_observation_key,
                e.identity_evidence_version = row.props.identity_evidence_version,
                e.identity_calendar_key = row.props.identity_calendar_key
            RETURN count(e) AS written";

    /// <summary>
    /// PR260-F1: the owner's merged-away or forgotten entities that its identity records point at, with how each was
    /// retired.
    /// </summary>
    public const string ExportRetiredIdentityEntities = @"
            MATCH (n:Entity) WHERE n.owner_id = $ownerId AND (n.invalidated_at IS NOT NULL OR n.merged_into IS NOT NULL)
              AND EXISTS { MATCH (n)-[:HAS_IDENTITY_EVIDENCE|RESOLVES_TO|POSSIBLE_SAME_AS]-(r) WHERE r.owner_id = $ownerId }
            RETURN n, n.invalidated_at AS invalidatedAt, n.merged_into AS mergedInto ORDER BY n.created_at, n.id";

    /// <summary>PR260-F9: the owner's retired entities among those a current decision's record names, linked or not.</summary>
    public const string ExportRetiredEntitiesById = @"
            MATCH (n:Entity) WHERE n.id IN $ids AND n.owner_id = $ownerId AND (n.invalidated_at IS NOT NULL OR n.merged_into IS NOT NULL)
            RETURN n, n.invalidated_at AS invalidatedAt, n.merged_into AS mergedInto ORDER BY n.created_at, n.id";

    /// <summary>PR260-F1: an imported retired entity's retirement, on the importing owner's entities only.</summary>
    public const string RestoreEntityRetirement = @"
            UNWIND $rows AS row
            MATCH (e:Entity {id: row.id}) WHERE e.owner_id = $ownerId
            SET e.invalidated_at = CASE WHEN row.invalidatedAt IS NULL THEN null ELSE datetime(row.invalidatedAt) END,
                e.merged_into = row.mergedInto
            RETURN count(e) AS written";

    /// <summary>
    /// PR260-F9/F10: the entities a file names that the import does not create. Each must be a shared entity here; one
    /// that belongs to an owner stops the import, and one that is missing leaves its link out.
    /// </summary>
    public const string ForeignEndpoints = @"
            UNWIND $ids AS id
            OPTIONAL MATCH (e:Entity {id: id})
            RETURN id, e IS NOT NULL AS present, e IS NOT NULL AND e.owner_id IS NULL AS shared";

    /// <summary>
    /// REV1-H2: which of the ids a file names are messages of the importing owner in this store; only those stay as they
    /// are, every other becomes a marker.
    /// </summary>
    public const string OwnMessages = @"
            UNWIND $ids AS id
            MATCH (m:Message {id: id})
            MATCH (c:Conversation {id: m.conversation_id}) WHERE c.user_id = $ownerId
            RETURN DISTINCT m.id AS id";

    /// <summary>
    /// REV3-6: removes what a failed import created, by kind, only the importing owner's; a relationship is found from its
    /// source entity.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> RemoveImported = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["RELATED_TO"] = "UNWIND $rows AS row MATCH (:Entity {id: row.from})-[r:RELATED_TO {id: row.id}]->() WHERE r.owner_id = $ownerId DELETE r",
        ["Entity"] = "UNWIND $rows AS row MATCH (n:Entity {id: row.id}) WHERE n.owner_id = $ownerId DETACH DELETE n",
        ["Fact"] = "UNWIND $rows AS row MATCH (n:Fact {id: row.id}) WHERE n.owner_id = $ownerId DETACH DELETE n",
        ["Preference"] = "UNWIND $rows AS row MATCH (n:Preference {id: row.id}) WHERE n.owner_id = $ownerId DETACH DELETE n",
        ["IdentityDecision"] = "UNWIND $rows AS row MATCH (n:IdentityDecision {id: row.id}) WHERE n.owner_id = $ownerId DETACH DELETE n",
        ["IdentityDecisionRevision"] = "UNWIND $rows AS row MATCH (n:IdentityDecisionRevision {id: row.id}) WHERE n.owner_id = $ownerId DETACH DELETE n",
        ["IdentityFactBinding"] = "UNWIND $rows AS row MATCH (n:IdentityFactBinding {id: row.id}) WHERE n.owner_id = $ownerId DETACH DELETE n",
        ["IdentityEvidenceAssertion"] = "UNWIND $rows AS row MATCH (n:IdentityEvidenceAssertion {id: row.id}) WHERE n.owner_id = $ownerId DETACH DELETE n",
        ["IdentityRelationshipObservation"] = "UNWIND $rows AS row MATCH (n:IdentityRelationshipObservation {id: row.id}) WHERE n.owner_id = $ownerId DETACH DELETE n",
    };

    /// <summary>REV2-10: how many of these ids already exist as messages.</summary>
    public const string ExistingMessages = @"
            UNWIND $ids AS id
            MATCH (m:Message {id: id})
            RETURN count(m) AS taken";

    /// <summary>REV2-14: the owner's fact in its open period with these keys, the one a write would merge onto.</summary>
    public const string HeldFact = @"
            MATCH (f:Fact {subject_key: $subjectKey, predicate_key: $predicateKey, object_key: $objectKey, owner_key: $ownerKey, period_key: ''})
            WHERE f.owner_id = $ownerId AND f.invalidated_at IS NULL
            RETURN f.id AS id LIMIT 1";

    /// <summary>PR260-F7: an imported decision's record and target fingerprint, rebuilt for its imported target.</summary>
    public const string ReadImportedDecision = @"
            MATCH (d:IdentityDecision {id: $id}) WHERE d.owner_id = $ownerId RETURN d.identity_record_json AS recordJson";

    /// <summary>PR260-F7: see <see cref=""ReadImportedDecision""/>.</summary>
    public const string RefreshImportedDecision = @"
            MATCH (d:IdentityDecision {id: $id}) WHERE d.owner_id = $ownerId
            SET d.identity_record_json = $recordJson, d.identity_target_snapshot = $targetSnapshot";

    /// <summary>
    /// ILC-30: every link identity storage makes (IdentityEvidenceSchemaExtension's relationship types), by the kinds of
    /// record it joins, with its export (the owner's side) and import. Identity links carry no properties. On import each
    /// end must be the importing owner's (or, for an entity, shared); the import has already refused any other (PR260-F9).
    /// </summary>
    internal static readonly IReadOnlyList<(string FromLabel, string Type, string ToLabel, string Export, string Import)> IdentityLinks =
    [
        ("Fact", "HAS_IDENTITY_BINDING", "IdentityFactBinding",
            "MATCH (a:Fact)-[r:HAS_IDENTITY_BINDING]->(b:IdentityFactBinding) WHERE b.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:Fact {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:IdentityFactBinding {id: row.to}) WHERE b.owner_id = $ownerId MERGE (a)-[r:HAS_IDENTITY_BINDING]->(b) RETURN count(r) AS written"),
        ("IdentityFactBinding", "DECIDED_BY", "IdentityDecision",
            "MATCH (a:IdentityFactBinding)-[r:DECIDED_BY]->(b:IdentityDecision) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityFactBinding {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:IdentityDecision {id: row.to}) WHERE b.owner_id = $ownerId MERGE (a)-[r:DECIDED_BY]->(b) RETURN count(r) AS written"),
        ("IdentityFactBinding", "RESOLVES_TO", "Entity",
            "MATCH (a:IdentityFactBinding)-[r:RESOLVES_TO]->(b:Entity) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityFactBinding {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:Entity {id: row.to}) WHERE (b.owner_id = $ownerId OR b.owner_id IS NULL) MERGE (a)-[r:RESOLVES_TO]->(b) RETURN count(r) AS written"),
        ("IdentityDecision", "RESOLVES_TO", "Entity",
            "MATCH (a:IdentityDecision)-[r:RESOLVES_TO]->(b:Entity) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityDecision {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:Entity {id: row.to}) WHERE (b.owner_id = $ownerId OR b.owner_id IS NULL) MERGE (a)-[r:RESOLVES_TO]->(b) RETURN count(r) AS written"),
        ("IdentityDecision", "HAS_IDENTITY_REVISION", "IdentityDecisionRevision",
            "MATCH (a:IdentityDecision)-[r:HAS_IDENTITY_REVISION]->(b:IdentityDecisionRevision) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityDecision {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:IdentityDecisionRevision {id: row.to}) WHERE b.owner_id = $ownerId MERGE (a)-[r:HAS_IDENTITY_REVISION]->(b) RETURN count(r) AS written"),
        ("IdentityDecision", "POSSIBLE_SAME_AS", "Entity",
            "MATCH (a:IdentityDecision)-[r:POSSIBLE_SAME_AS]->(b:Entity) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityDecision {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:Entity {id: row.to}) WHERE (b.owner_id = $ownerId OR b.owner_id IS NULL) MERGE (a)-[r:POSSIBLE_SAME_AS]->(b) RETURN count(r) AS written"),
        ("Entity", "HAS_IDENTITY_EVIDENCE", "IdentityEvidenceAssertion",
            "MATCH (a:Entity)-[r:HAS_IDENTITY_EVIDENCE]->(b:IdentityEvidenceAssertion) WHERE b.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:Entity {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:IdentityEvidenceAssertion {id: row.to}) WHERE b.owner_id = $ownerId MERGE (a)-[r:HAS_IDENTITY_EVIDENCE]->(b) RETURN count(r) AS written"),
        ("IdentityRelationshipObservation", "SOURCE_IDENTITY", "IdentityDecision",
            "MATCH (a:IdentityRelationshipObservation)-[r:SOURCE_IDENTITY]->(b:IdentityDecision) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityRelationshipObservation {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:IdentityDecision {id: row.to}) WHERE b.owner_id = $ownerId MERGE (a)-[r:SOURCE_IDENTITY]->(b) RETURN count(r) AS written"),
        ("IdentityRelationshipObservation", "TARGET_IDENTITY", "IdentityDecision",
            "MATCH (a:IdentityRelationshipObservation)-[r:TARGET_IDENTITY]->(b:IdentityDecision) WHERE a.owner_id = $ownerId RETURN a.id AS from, b.id AS to, properties(r) AS props",
            "UNWIND $rows AS row MATCH (a:IdentityRelationshipObservation {id: row.from}) WHERE a.owner_id = $ownerId MATCH (b:IdentityDecision {id: row.to}) WHERE b.owner_id = $ownerId MERGE (a)-[r:TARGET_IDENTITY]->(b) RETURN count(r) AS written"),
    ];

    /// <summary>An imported supersession.</summary>
    public const string LinkSupersession = @"
            MATCH (l:Fact {id: $from}), (w:Fact {id: $to})
            MERGE (l)-[:SUPERSEDED_BY]->(w)";
}
