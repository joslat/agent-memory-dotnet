namespace AgentMemory.Neo4j.Queries;

/// <summary>Scoped immutable review revisions and rewiring of decision-owned support only.</summary>
internal static class IdentityReviewQueries
{
    internal const string GetOperation = """
        MATCH (r:IdentityDecisionRevision {id:$revisionId,owner_scope:$ownerScope})
        RETURN r.identity_record_json AS recordJson
        """;

    internal const string History = """
        MATCH (d:IdentityDecision {id:$decisionId,owner_scope:$ownerScope})-[:HAS_IDENTITY_REVISION]->(r:IdentityDecisionRevision)
        WHERE r.owner_scope=$ownerScope AND ($beforeVersion IS NULL OR r.identity_revision<$beforeVersion)
        RETURN r.identity_record_json AS recordJson ORDER BY r.identity_revision DESC LIMIT $limit
        """;

    internal const string ReadBindings = """
        MATCH (b:IdentityFactBinding {identity_decision_id:$decisionId,owner_scope:$ownerScope})
        OPTIONAL MATCH (f:Fact)-[:HAS_IDENTITY_BINDING]->(b)
        RETURN b.id AS id,b.identity_fact_role AS role,collect(DISTINCT f.id) AS facts
        ORDER BY id LIMIT $limit
        """;

    internal const string ClearBindingTarget = """
        MATCH (b:IdentityFactBinding {id:$bindingId,identity_decision_id:$decisionId,owner_scope:$ownerScope})
              -[:DECIDED_BY]->(d:IdentityDecision {id:$decisionId,owner_scope:$ownerScope})
        WHERE ($ownerId IS NULL AND b.owner_id IS NULL) OR b.owner_id=$ownerId
        OPTIONAL MATCH (b)-[old:RESOLVES_TO]->(:Entity)
        WITH b,collect(old) AS previous WHERE size(previous)=1
        FOREACH (edge IN previous | DELETE edge)
        RETURN b.id AS id
        """;

    internal const string SetDecisionTarget = """
        MATCH (d:IdentityDecision {id:$decisionId,owner_scope:$ownerScope}),(t:Entity {id:$targetId})
        WHERE t.invalidated_at IS NULL AND t.merged_into IS NULL AND
            (($ownerId IS NULL AND t.owner_id IS NULL) OR
             ($ownerId IS NOT NULL AND (t.owner_id=$ownerId OR ($includeShared AND t.owner_id IS NULL))))
        OPTIONAL MATCH (d)-[old:RESOLVES_TO]->(:Entity)
        WITH d,t,collect(old) AS previous WHERE size(previous)<=1
        FOREACH (edge IN previous | DELETE edge)
        MERGE (d)-[:RESOLVES_TO]->(t)
        RETURN d.id AS id
        """;

    internal const string ClearProposals = """
        MATCH (d:IdentityDecision {id:$decisionId,owner_scope:$ownerScope})-[r:POSSIBLE_SAME_AS]->()
        DELETE r
        """;

    internal const string CommitRevision = """
        MATCH (d:IdentityDecision {id:$decisionId,owner_scope:$ownerScope})
        WHERE d.identity_revision=$expectedVersion
        SET d.identity_record_json=$recordJson,d.identity_target_snapshot=$targetSnapshot,d.identity_revision=$version
        CREATE (r:IdentityDecisionRevision {id:$revisionId,owner_scope:$ownerScope,owner_id:$ownerId,
            identity_record_json:$recordJson,identity_revision:$version,created_at:datetime($reviewedAt)})
        CREATE (d)-[:HAS_IDENTITY_REVISION]->(r)
        RETURN d.id AS id
        """;
}
