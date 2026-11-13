namespace AgentMemory.Neo4j.Queries;

/// <summary>Fail closed on pre-upgrade key collisions before any MERGE mutates a foreign owner.</summary>
internal static class OwnerScopeGuards
{
    internal const string FactSingle = """
        WITH 1 AS scope_guard
        WHERE NOT EXISTS {
            MATCH (foreign:Fact {subject_key: $subjectKey, predicate_key: $predicateKey, object_key: $objectKey, owner_key: $ownerKey, period_key: ''})
            WHERE ($ownerId IS NULL AND foreign.owner_id IS NOT NULL) OR
                ($ownerId IS NOT NULL AND (foreign.owner_id IS NULL OR foreign.owner_id <> $ownerId))
        }

        """;

    internal const string FactBatch = """
        WITH 1 AS scope_guard
        WHERE NOT EXISTS {
            UNWIND $items AS candidate
            MATCH (foreign:Fact {subject_key: candidate.subject_key, predicate_key: candidate.predicate_key,
                object_key: candidate.object_key, owner_key: candidate.owner_key, period_key: ''})
            WHERE (candidate.owner_id IS NULL AND foreign.owner_id IS NOT NULL) OR
                (candidate.owner_id IS NOT NULL AND (foreign.owner_id IS NULL OR foreign.owner_id <> candidate.owner_id))
        }

        """;

    internal const string Summary = """
        WITH 1 AS scope_guard
        WHERE NOT EXISTS {
            MATCH (foreign:EntitySummary {entity_id: $entityId, owner_key: $ownerKey})
            WHERE ($ownerId IS NULL AND foreign.owner_id IS NOT NULL) OR
                ($ownerId IS NOT NULL AND (foreign.owner_id IS NULL OR foreign.owner_id <> $ownerId))
        }

        """;
}
