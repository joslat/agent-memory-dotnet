using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher for normalized memory-history reads over long-term memory nodes.
/// </summary>
internal static class HistoryQueries
{
    public static string List(MemoryHistoryKind? kind)
    {
        var segments = kind switch
        {
            MemoryHistoryKind.Entity => new[] { EntitySegment },
            MemoryHistoryKind.Fact => new[] { FactSegment },
            MemoryHistoryKind.Preference => new[] { PreferenceSegment },
            null => new[] { EntitySegment, FactSegment, PreferenceSegment },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported memory history kind."),
        };

        return "CALL {\n" + string.Join("\nUNION ALL\n", segments) + @"
}
RETURN kind, id, summary, ownerId, createdAt, updatedAt, invalidatedAt, lastAccessedAt, accessCount,
       readAuditCount, lastReadAuditAt, validFrom, validUntil, sourceMessageIds, supersededByIds,
       supersedesIds, metadata
ORDER BY coalesce(lastReadAuditAt, lastAccessedAt, invalidatedAt, updatedAt, createdAt) DESC, id ASC
LIMIT $limit";
    }

    private const string OwnerAndLifecycleWhere = @"
WHERE ($id IS NULL OR n.id = $id)
  AND ($includeInvalidated OR n.invalidated_at IS NULL)
  AND ($ownerId IS NULL OR n.owner_id = $ownerId OR ($includeShared AND n.owner_id IS NULL))";

    private const string EntitySegment = @"
MATCH (n:Entity)
" + OwnerAndLifecycleWhere + @"
OPTIONAL MATCH (n)-[:SUPERSEDED_BY]->(winner)
WITH n, collect(distinct winner.id) AS supersededByIds
OPTIONAL MATCH (loser)-[:SUPERSEDED_BY]->(n)
WITH n, supersededByIds, collect(distinct loser.id) AS supersedesIds
OPTIONAL MATCH (n)-[:EXTRACTED_FROM]->(m:Message)
WITH n, supersededByIds, supersedesIds, collect(distinct m.id) AS extractedMessageIds
OPTIONAL MATCH (audit:MemoryReadAudit {memory_id: n.id})
WITH n, supersededByIds, supersedesIds, extractedMessageIds,
     count(audit) AS readAuditCount, max(audit.read_at) AS lastReadAuditAt
RETURN 'Entity' AS kind,
       n.id AS id,
       trim(coalesce(n.name, '') + CASE WHEN n.type IS NULL THEN '' ELSE ' (' + n.type + ')' END) AS summary,
       n.owner_id AS ownerId,
       n.created_at AS createdAt,
       n.updated_at AS updatedAt,
       n.invalidated_at AS invalidatedAt,
       n.last_accessed_at AS lastAccessedAt,
       coalesce(n.access_count, 0) AS accessCount,
       readAuditCount,
       lastReadAuditAt,
       null AS validFrom,
       null AS validUntil,
       coalesce(n.source_message_ids, []) + extractedMessageIds AS sourceMessageIds,
       supersededByIds,
       supersedesIds,
       n.metadata AS metadata";

    private const string FactSegment = @"
MATCH (n:Fact)
" + OwnerAndLifecycleWhere + @"
OPTIONAL MATCH (n)-[:SUPERSEDED_BY]->(winner)
WITH n, collect(distinct winner.id) AS supersededByIds
OPTIONAL MATCH (loser)-[:SUPERSEDED_BY]->(n)
WITH n, supersededByIds, collect(distinct loser.id) AS supersedesIds
OPTIONAL MATCH (n)-[:EXTRACTED_FROM]->(m:Message)
WITH n, supersededByIds, supersedesIds, collect(distinct m.id) AS extractedMessageIds
OPTIONAL MATCH (audit:MemoryReadAudit {memory_id: n.id})
WITH n, supersededByIds, supersedesIds, extractedMessageIds,
     count(audit) AS readAuditCount, max(audit.read_at) AS lastReadAuditAt
RETURN 'Fact' AS kind,
       n.id AS id,
       trim(coalesce(n.subject, '') + ' ' + coalesce(n.predicate, '') + ' ' + coalesce(n.object, '')) AS summary,
       n.owner_id AS ownerId,
       n.created_at AS createdAt,
       n.updated_at AS updatedAt,
       n.invalidated_at AS invalidatedAt,
       n.last_accessed_at AS lastAccessedAt,
       coalesce(n.access_count, 0) AS accessCount,
       readAuditCount,
       lastReadAuditAt,
       n.valid_from AS validFrom,
       n.valid_until AS validUntil,
       coalesce(n.source_message_ids, []) + extractedMessageIds AS sourceMessageIds,
       supersededByIds,
       supersedesIds,
       n.metadata AS metadata";

    private const string PreferenceSegment = @"
MATCH (n:Preference)
" + OwnerAndLifecycleWhere + @"
OPTIONAL MATCH (n)-[:SUPERSEDED_BY]->(winner)
WITH n, collect(distinct winner.id) AS supersededByIds
OPTIONAL MATCH (loser)-[:SUPERSEDED_BY]->(n)
WITH n, supersededByIds, collect(distinct loser.id) AS supersedesIds
OPTIONAL MATCH (n)-[:EXTRACTED_FROM]->(m:Message)
WITH n, supersededByIds, supersedesIds, collect(distinct m.id) AS extractedMessageIds
OPTIONAL MATCH (audit:MemoryReadAudit {memory_id: n.id})
WITH n, supersededByIds, supersedesIds, extractedMessageIds,
     count(audit) AS readAuditCount, max(audit.read_at) AS lastReadAuditAt
RETURN 'Preference' AS kind,
       n.id AS id,
       trim(coalesce(n.category, '') + CASE WHEN n.category IS NULL OR n.preference IS NULL THEN '' ELSE ': ' END + coalesce(n.preference, '')) AS summary,
       n.owner_id AS ownerId,
       n.created_at AS createdAt,
       n.updated_at AS updatedAt,
       n.invalidated_at AS invalidatedAt,
       n.last_accessed_at AS lastAccessedAt,
       coalesce(n.access_count, 0) AS accessCount,
       readAuditCount,
       lastReadAuditAt,
       null AS validFrom,
       null AS validUntil,
       coalesce(n.source_message_ids, []) + extractedMessageIds AS sourceMessageIds,
       supersededByIds,
       supersedesIds,
       n.metadata AS metadata";
}
