// SPDX-License-Identifier: MIT
namespace AgentMemory.Neo4j.Queries;

internal static class PackLocalQueries
{
    internal const string Ready = "SHOW CONSTRAINTS YIELD name WHERE name IN ['pack_local_mount_id','pack_local_change_id','pack_local_revision_id','pack_local_gate_id'] RETURN count(*) AS count";
    internal const string Gate = "MERGE (g:PackLocalGate {id:$pack}) SET g.lock_token=true REMOVE g.lock_token RETURN g.id AS id";
    internal const string ErasureCandidates = """
        MATCH (p:PackLocalMount {pack_id:$pack}), (c:PackLocalChange {mount_id:p.id})
        RETURN c.id AS id,c.type AS type,c.key AS key,c.payload AS payload LIMIT 100001
        """;
    internal const string PurgeWithdrawn = "MATCH (c:PackLocalChange) WHERE c.id IN $ids DETACH DELETE c";
    internal const string Sources = """
        MATCH (c:Conversation)-[:HAS_MESSAGE]->(m:Message)
        WHERE m.id IN $ids AND c.user_id=$owner AND m.session_id=c.session_id AND m.conversation_id=c.id
          AND m.invalidated_at IS NULL AND m.role='user'
        RETURN DISTINCT m.id AS id,m.content AS content
        """;
    internal const string Tombstones = """
        MATCH (t:ModuleTombstone {pack:$pack})
        RETURN t.type AS type,t.salt AS salt,t.key_hash AS hash,t.erasure AS statement,t.reason AS reason,t.erased_at AS at
        LIMIT 100001
        """;
    internal const string Mount = """
        MERGE (p:PackLocalMount {id:$id})
        ON CREATE SET p.application_id=$application, p.owner_id=$owner, p.pack_id=$pack, p.base_digest=$digest,p.base_version=$version,
          p.write_protected=$protected, p.automatic_learning=false, p.active=true, p.revision=0
        SET p.lock_token=true REMOVE p.lock_token RETURN properties(p) AS policy
        """;
    internal const string Lock = """
        MATCH (p:PackLocalMount {id:$id,owner_id:$owner,application_id:$application,pack_id:$pack})
        SET p.lock_token=true REMOVE p.lock_token RETURN properties(p) AS policy
        """;
    internal const string Read = """
        MATCH (p:PackLocalMount {id:$id,owner_id:$owner,application_id:$application,pack_id:$pack})
        CALL { WITH p MATCH (c:PackLocalChange {mount_id:p.id})
          WHERE ($asOf IS NULL AND c.invalidated_at IS NULL)
             OR ($asOf IS NOT NULL AND c.created_at<=datetime($asOf) AND (c.invalidated_at IS NULL OR c.invalidated_at>datetime($asOf)))
          WITH c ORDER BY c.type,c.key LIMIT 5001 RETURN collect(properties(c)) AS changes }
        RETURN properties(p) AS policy,changes
        """;
    internal const string ReadAsOf = """
        MATCH (p:PackLocalMount {id:$id,owner_id:$owner,application_id:$application,pack_id:$pack})
        MATCH (v:PackLocalRevision {mount_id:p.id})
        WHERE v.created_at<=datetime($asOf) AND (v.invalidated_at IS NULL OR v.invalidated_at>datetime($asOf))
        WITH p,v ORDER BY v.revision DESC LIMIT 1
        CALL { WITH p MATCH (c:PackLocalChange {mount_id:p.id})
          WHERE c.created_at<=datetime($asOf) AND (c.invalidated_at IS NULL OR c.invalidated_at>datetime($asOf))
          WITH c ORDER BY c.type,c.key LIMIT 5001 RETURN collect(properties(c)) AS changes }
        RETURN properties(v) AS policy,changes
        """;
    internal const string RecordRevision = """
        MATCH (p:PackLocalMount {id:$id})
        OPTIONAL MATCH (old:PackLocalRevision {mount_id:p.id}) WHERE old.invalidated_at IS NULL
        WITH p,old WHERE old IS NULL OR old.revision<>p.revision
        CREATE (n:PackLocalRevision {id:$historyId,mount_id:p.id,base_digest:p.base_digest,base_version:p.base_version,
          write_protected:p.write_protected,automatic_learning:p.automatic_learning,active:p.active,revision:p.revision,created_at:datetime($now)})
        FOREACH (prior IN CASE WHEN old IS NULL THEN [] ELSE [old] END |
          SET prior.invalidated_at=datetime($now),prior.valid_until=datetime($now) MERGE (prior)-[:SUPERSEDED_BY]->(n))
        RETURN n.id AS id
        """;
    internal const string Policy = """
        MATCH (p:PackLocalMount {id:$id}) SET p.write_protected=$protected,p.automatic_learning=$automatic,
          p.active=$active,p.revision=p.revision+1 RETURN properties(p) AS policy
        """;
    internal const string ReadPolicy = "MATCH (p:PackLocalMount {id:$id,owner_id:$owner,application_id:$application,pack_id:$pack}) RETURN properties(p) AS policy";
    internal const string ReadStatuses = """
        MATCH (p:PackLocalMount)
        WHERE p.id IN $ids AND p.owner_id=$owner AND p.application_id=$application AND p.pack_id IN $packs
        RETURN p.pack_id AS pack,properties(p) AS policy,
          EXISTS { MATCH (c:PackLocalChange {mount_id:p.id}) WHERE c.invalidated_at IS NULL AND c.payload IS NOT NULL } AS amended
        LIMIT 33
        """;
    internal const string Rebase = "MATCH (p:PackLocalMount {id:$id}) SET p.base_digest=$digest,p.base_version=$version,p.active=true,p.revision=p.revision+1 RETURN properties(p) AS policy";
    // A detached mount loaded again (the host lifecycle, R-HOST-069): the protection the owner had before the detach is kept;
    // where the copy was writable then, the host's request applies. Without that history (a mount from before it), it stays.
    internal const string Reactivate = """
        MATCH (p:PackLocalMount {id:$id})
        OPTIONAL MATCH (before:PackLocalRevision {mount_id:p.id})-[:SUPERSEDED_BY]->(detached:PackLocalRevision {mount_id:p.id})
          WHERE detached.invalidated_at IS NULL AND detached.active=false AND before.active=true
        WITH p, before
        SET p.base_digest=$digest,p.base_version=$version,p.active=true,p.revision=p.revision+1,
          p.write_protected=CASE WHEN before IS NOT NULL AND before.write_protected=false THEN $protected ELSE p.write_protected END
        RETURN properties(p) AS policy
        """;
    internal const string Change = """
        MATCH (p:PackLocalMount {id:$id})
        OPTIONAL MATCH (old:PackLocalChange {mount_id:$id,type:$type,key:$key}) WHERE old.invalidated_at IS NULL
        CREATE (n:PackLocalChange {id:$changeId,mount_id:$id,type:$type,key:$key,actor:$actor,base_digest:$digest,
          payload:$payload,created_at:datetime($now)})
        FOREACH (prior IN CASE WHEN old IS NULL THEN [] ELSE [old] END |
          SET prior.invalidated_at=datetime($now),prior.valid_until=datetime($now) MERGE (prior)-[:SUPERSEDED_BY]->(n))
        SET p.revision=p.revision+1 RETURN n.id AS id
        """;
    internal const string History = """
        MATCH (p:PackLocalMount {id:$id,owner_id:$owner,application_id:$application,pack_id:$pack}),
          (c:PackLocalChange {mount_id:p.id,type:$type,key:$key})
        RETURN properties(c) AS row ORDER BY c.created_at DESC,c.id DESC LIMIT 1000
        """;
}
