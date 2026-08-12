namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher for synthesized entity summaries (S1).
/// </summary>
/// <remarks>
/// <para>
/// One summary per entity, held on a <c>:EntitySummary</c> node joined to its entity by
/// <c>:SUMMARISES</c>. A separate node rather than a property on <c>:Entity</c> because it carries
/// its own provenance edges and its own fingerprint, and because a derived value living beside
/// extracted ones invites exactly the confusion this design is trying to avoid.
/// </para>
/// <para>
/// <b>No history.</b> A superseded summary is not a record of what was once believed — the facts it
/// was built from carry their own bitemporal history already, and keeping stale derivations around
/// would mean two answers to "what did we think about Alice in March", only one of them authoritative.
/// </para>
/// </remarks>
internal static class EntitySummaryQueries
{
    /// <summary>
    /// Writes the entity's summary, replacing whatever was there.
    /// </summary>
    /// <remarks>
    /// MERGE on <c>entity_id</c> + <c>owner_key</c>, so two tenants summarising the same shared entity
    /// keep separate summaries — each was synthesised from the facts that tenant can see, and sharing
    /// one node would leak one owner's knowledge into another's context.
    /// </remarks>
    public const string Upsert = """
        MERGE (s:EntitySummary {entity_id: $entityId, owner_key: $ownerKey})
        SET s.id                 = $summaryId,
            s.content            = $content,
            s.source_fact_ids    = $sourceFactIds,
            s.source_fingerprint = $sourceFingerprint,
            s.owner_id           = $ownerId,
            s.generated_at       = datetime($generatedAt)
        WITH s
        OPTIONAL MATCH (e:Entity {id: $entityId})
        FOREACH (_ IN CASE WHEN e IS NULL THEN [] ELSE [1] END |
            MERGE (s)-[:SUMMARISES]->(e))
        WITH s
        // Provenance to the facts the text was written from. Rebuilt wholesale rather than merged,
        // because a summary regenerated from a smaller fact set must not keep edges to sources it no
        // longer draws on -- that would claim provenance the content cannot support.
        OPTIONAL MATCH (s)-[old:EXTRACTED_FROM]->(:Fact)
        DELETE old
        WITH s
        UNWIND $sourceFactIds AS factId
        MATCH (f:Fact {id: factId})
        MERGE (s)-[:EXTRACTED_FROM]->(f)
        RETURN s
        """;

    /// <summary>Reads an entity's summary, owner-confined (R1).</summary>
    public static string GetByEntity(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (s.owner_id = $ownerId OR s.owner_id IS NULL)"
                            : " AND s.owner_id = $ownerId";
        return $"MATCH (s:EntitySummary) WHERE s.entity_id = $entityId{owner} RETURN s";
    }

    /// <summary>Removes an entity's summary and its edges.</summary>
    public static string DeleteByEntity(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (s.owner_id = $ownerId OR s.owner_id IS NULL)"
                            : " AND s.owner_id = $ownerId";
        return $"""
            MATCH (s:EntitySummary) WHERE s.entity_id = $entityId{owner}
            WITH s, count(s) AS found
            DETACH DELETE s
            RETURN found
            """;
    }
}
