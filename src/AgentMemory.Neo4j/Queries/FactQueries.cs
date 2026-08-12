using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Fact operations.
/// Each constant corresponds to exactly one repository method in
/// <see cref="AgentMemory.Neo4j.Repositories.Neo4jFactRepository"/>.
/// </summary>
internal static class FactQueries
{
    // ── Canonical-key backfill (Phase 1.1) ─────────────────────────────

    /// <summary>Facts written before canonical identity, in bounded batches.</summary>
    /// <remarks>
    /// Selecting on <c>predicate_key IS NULL</c> makes the backfill idempotent by construction: once
    /// every fact is keyed, a re-run selects nothing. Bounded so a large store migrates in batches
    /// rather than one transaction.
    /// </remarks>
    public const string SelectFactsMissingCanonicalKeys = @"
            MATCH (f:Fact)
            WHERE f.predicate_key IS NULL
            RETURN f.id AS id, f.subject AS subject, f.predicate AS predicate, f.object AS object
            LIMIT $limit";

    /// <summary>
    /// Writes canonical keys computed <b>in C#</b> onto facts identified by id.
    /// </summary>
    /// <remarks>
    /// Deliberately contains no <c>toLower()</c> or string rewriting: Cypher's <c>toLower()</c> and
    /// .NET's <c>ToLowerInvariant()</c> disagree on U+0130, so a key computed here would not match
    /// the one the write path produces, silently reintroducing the duplication canonical identity
    /// exists to remove. That is also why this is not a .cypher migration file.
    /// </remarks>
    public const string ApplyCanonicalKeys = @"
            UNWIND $items AS item
            MATCH (f:Fact {id: item.id})
            SET f.subject_key   = item.subject_key,
                f.predicate_key = item.predicate_key,
                f.object_key    = item.object_key
            RETURN count(f) AS updated";

    // ── Predicate expansion (G3B.13) ───────────────────────────────────

    /// <summary>
    /// Every fact an owner holds under the given canonical predicates, bounded.
    /// </summary>
    /// <remarks>
    /// Top-K vector search answers "what is most relevant"; it cannot answer "how many", because a
    /// relevance cutoff gives no completeness guarantee — miss one of five births and the count is
    /// four. This retrieves a <i>relation</i> whole so aggregation questions become answerable, and
    /// composes with top-K rather than replacing it: similarity finds which predicate matters, this
    /// makes that predicate complete.
    /// <para>
    /// Matches <c>predicate_key</c>, never the raw predicate: raw text would reinstate the exact
    /// fragmentation canonical identity removed, where "were_born_in" and "were born in" fail to
    /// find each other. Owner-scoped and explicitly limited — unbounded completeness over a graph of
    /// ~1,000 facts would simply exhaust the answer budget.
    /// </para>
    /// </remarks>
    public static string SearchByCanonicalPredicates(
        bool hasOwnerFilter,
        bool includeShared,
        bool hasPriorityKeys = false)
    {
        // Mirrors GetBySubject's owner-conditional shape rather than inventing its own. The first
        // version hard-coded `f.owner_key = $ownerKey` with `scope.OwnerId ?? OwnerKeyShared`, which
        // (a) never matched shared facts even when IncludeShared was set, silently breaking the
        // "relation whole" guarantee, and (b) coerced a null-owner scope to the shared bucket, so it
        // returned nothing exactly where top-K returned everything.
        // J3.1. One shared LIMIT covers the question's resolved relations AND the canonical predicate
        // of every top-K vector hit, ordered globally by confidence, so unrelated high-confidence
        // facts consume the budget before the relation the question named is exhausted. Measured:
        // a9f6b44c holds 49 facts under planned/plans, had a 60-row budget, and received 22.
        //
        // Ordering the question's own keys first is a TIEBREAK, not a filter: it changes nothing
        // when the budget is not binding, and it never widens what is visible - the WHERE clause is
        // untouched. Empty when no priority keys are supplied, so every existing caller gets the
        // byte-identical query it had before.
        var priority = hasPriorityKeys
            ? "CASE WHEN f.predicate_key IN $priorityKeys THEN 0 ELSE 1 END, "
            : string.Empty;
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (f.owner_id = $ownerId OR f.owner_id IS NULL)"
                            : " AND f.owner_id = $ownerId";
        return $@"
            MATCH (f:Fact)
            WHERE f.predicate_key IN $predicateKeys
              AND f.invalidated_at IS NULL{owner}
            RETURN f
            ORDER BY {priority}f.confidence DESC, f.id ASC
            LIMIT $limit";
    }

    // ── UpsertAsync ────────────────────────────────────────────────────

    /// <summary>Merge a fact by subject/predicate/object triple, setting all properties.</summary>
    public const string Upsert = @"
            MERGE (f:Fact {subject_key: $subjectKey, predicate_key: $predicateKey, object_key: $objectKey, owner_key: $ownerKey})
            ON CREATE SET
                f.subject            = $subject,
                f.predicate          = $predicate,
                f.object             = $object,
                f.id                 = $id,
                f.owner_id           = $ownerId,
                f.category           = $category,
                f.confidence         = $confidence,
                f.valid_from         = CASE WHEN $validFrom IS NOT NULL THEN datetime($validFrom) ELSE null END,
                f.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE null END,
                f.source_message_ids = $sourceMessageIds,
                f.created_at         = datetime($createdAtUtc),
                f.metadata           = $metadata
            ON MATCH SET
                f.category           = $category,
                f.confidence         = $confidence,
                f.valid_from         = CASE WHEN $validFrom IS NOT NULL THEN datetime($validFrom) ELSE f.valid_from END,
                f.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE f.valid_until END,
                f.source_message_ids = $sourceMessageIds,
                f.updated_at         = datetime($updatedAtUtc),
                f.metadata           = $metadata,
                f.invalidated_at     = null
            RETURN f";

    // ── UpsertBatchAsync ───────────────────────────────────────────────

    /// <summary>
    /// Batch merge facts by the {subject, predicate, object, owner_key} triple via UNWIND — the SAME
    /// idempotency key as the single-fact <see cref="Upsert"/> path, so both surfaces collapse duplicate
    /// triples identically (R5 #10; previously this MERGEd on {id}, which let a re-extracted triple with a
    /// fresh id create a second node). Like the single path: <c>id</c> is set ON CREATE only (a matched node
    /// keeps its original primary key); subject/predicate/object/owner_id are immutable once set (they are
    /// the merge key or a function of it); valid_from/valid_until are coalesced on ON MATCH (preserve
    /// existing when incoming is null, so re-extraction never clears a supersession window); and
    /// invalidated_at is reset on re-assert (a present-time positive assertion restores live recall).
    /// </summary>
    public const string UpsertBatch = @"
            UNWIND $items AS item
            MERGE (f:Fact {subject_key: item.subject_key, predicate_key: item.predicate_key, object_key: item.object_key, owner_key: item.owner_key})
            ON CREATE SET
                f.subject            = item.subject,
                f.predicate          = item.predicate,
                f.object             = item.object,
                f.id                 = item.id,
                f.owner_id           = item.owner_id,
                f.category           = item.category,
                f.confidence         = item.confidence,
                f.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE null END,
                f.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE null END,
                f.source_message_ids = item.source_message_ids,
                f.created_at         = datetime(item.created_at),
                f.metadata           = item.metadata
            ON MATCH SET
                f.category           = item.category,
                f.confidence         = item.confidence,
                f.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE f.valid_from END,
                f.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE f.valid_until END,
                f.source_message_ids = item.source_message_ids,
                f.updated_at         = datetime(item.updated_at),
                f.metadata           = item.metadata,
                f.invalidated_at     = null
            RETURN f";

    // ── GetByIdAsync ───────────────────────────────────────────────────

    /// <summary>Get a single fact by id.</summary>
    public const string GetById = "MATCH (f:Fact {id: $id}) RETURN f";

    // ── GetBySubjectAsync ──────────────────────────────────────────────

    /// <summary>Get all facts for a given subject, with an optional owner/shared filter (R1).</summary>
    public static string GetBySubject(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (f.owner_id = $ownerId OR f.owner_id IS NULL)"
                            : " AND f.owner_id = $ownerId";
        return $"MATCH (f:Fact) WHERE f.subject = $subject{owner} RETURN f";
    }

    // ── Write-time supersession (M1) ───────────────────────────────────

    /// <summary>
    /// The <b>live</b> facts that assert a different object for the same subject and predicate, which a
    /// newly written fact about a functional relation replaces.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Liveness is filtered here rather than in memory for the reason every other liveness filter is:
    /// <c>invalidated_at</c> is not carried on the domain record, so a caller holding a
    /// <c>Fact</c> cannot tell a closed one from a live one. Fetching every fact for a subject and
    /// filtering afterwards would re-supersede already-closed facts — harmless to their timestamps,
    /// which <c>coalesce</c> protects, but it would accumulate a second <c>:SUPERSEDED_BY</c> edge per
    /// arrival and turn a chain into a fan.
    /// </para>
    /// <para>
    /// Excludes the winner by id: a fact cannot supersede itself, and the MERGE-on-triple write path
    /// means the incoming fact is already stored when this runs.
    /// </para>
    /// <para>
    /// (A method, not a const, so it is excluded from the Cypher snapshot inventory.)
    /// </para>
    /// </remarks>
    public static string FindSupersededCandidates(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (f.owner_id = $ownerId OR f.owner_id IS NULL)"
                            : " AND f.owner_id = $ownerId";
        return @"
            MATCH (f:Fact)
            WHERE f.subject_key = $subjectKey
              AND f.predicate_key = $predicateKey
              AND f.object_key <> $objectKey
              AND f.id <> $winnerId
              AND f.invalidated_at IS NULL" + owner + @"
            RETURN f
            ORDER BY f.created_at DESC";
    }

    // ── Dedup-on-create ────────────────────────────────────────────────

    /// <summary>
    /// Scopes live candidates to the same owner + case-insensitive subject/predicate before exact cosine
    /// scoring, then returns the best match above <c>$threshold</c>. Scoped exact scoring gives a caller
    /// holding the process-local dedup lock read-after-commit behavior without vector-index refresh lag.
    /// </summary>
    public static string FindDuplicate() => @"
            MATCH (node:Fact)
            WHERE node.invalidated_at IS NULL
              AND node.owner_key = $ownerKey
              AND toLower(node.subject) = toLower($subject)
              AND toLower(node.predicate) = toLower($predicate)
              AND node.embedding IS NOT NULL
            WITH node, vector.similarity.cosine(node.embedding, $embedding) AS score
            WHERE score >= $threshold
            RETURN node, score
            ORDER BY score DESC
            LIMIT 1";

    /// <summary>Reinforce an existing fact reached by dedup: bump its confidence.</summary>
    public const string MarkDeduplicated = "MATCH (f:Fact {id: $id}) SET f.confidence = $confidence RETURN f";

    // ── SearchByVectorAsync ────────────────────────────────────────────

    /// <summary>
    /// Vector similarity search on fact embeddings, with an optional owner/shared filter (R1).
    /// Over-fetches <paramref name="topK"/> candidates then LIMITs to <c>$limit</c> after filtering,
    /// which <b>reduces but does not remove</b> starvation by higher-scoring foreign rows. This comment
    /// previously claimed the owner filter "is never starved"; that claim was measured and is false —
    /// on a 50-owner corpus the querying owner received a mean of 7 of 60 candidates, and one question
    /// received none at all. See <c>OwnerVectorOverFetch</c>, which escalates once when a scoped search
    /// returns empty. When
    /// <paramref name="recencyRerank"/> is set (D1) the clamped ACT-R retention score is blended into the
    /// order key (<c>$tmpWeight</c>); when unset the query is byte-for-byte today's semantic-only ranking.
    /// </summary>
    public static string SearchByVector(
        bool hasOwnerFilter, bool includeShared, int topK, bool recencyRerank = false, bool currentValidTime = false) =>
        VectorRerank.Finish(
            new CypherBuilder()
                .WithVectorSearch("fact_embedding_idx", "$embedding", "node", topK)
                .Where("score >= $minScore")
                .And("node.invalidated_at IS NULL")
                // Valid time, copied VERBATIM from TemporalQueries so the two clocks cannot drift: the
                // as-of path has filtered on exactly this for as long as it has existed, while live
                // recall filtered on similarity, invalidated_at and owner and nothing else. A fact valid
                // from six months hence was returned TODAY, and one whose valid_until had passed was
                // returned FOREVER. Off unless asked for, so no deployment silently recalls less.
                .And("(node.valid_from IS NULL OR node.valid_from <= datetime($now))", when: currentValidTime)
                .And("(node.valid_until IS NULL OR node.valid_until > datetime($now))", when: currentValidTime)
                .And(includeShared ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)" : "node.owner_id = $ownerId", when: hasOwnerFilter),
            recencyRerank);

    /// <summary>
    /// Last-resort owner-scoped similarity search that does NOT use the global vector index.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The indexed path asks for a <b>global</b> top-K and then filters to the owner, so it starves
    /// once enough foreign rows outrank the owner's. Widening rescues that only up to a point:
    /// <c>EscalatedTopK</c> multiplies by 8 and is capped at <c>MaxTopK</c> = 2,000, so <b>once more
    /// than 2,000 foreign rows outrank the owner's, no widening can reach them.</b> Measured on a
    /// 4-dimensional index: the owner received 4 of 4 at 3,000 competing rows and <b>0 of 4 at
    /// 4,000</b>. The production corpus holds 36,489 facts.
    /// </para>
    /// <para>
    /// This query scores the owner's own facts directly with <c>vector.similarity.cosine</c>. It is a
    /// scan, deliberately — but a scan <b>bounded by one owner's data</b>, not by the corpus, and it
    /// is reached only when the indexed path and its escalation have both returned nothing. That
    /// makes it O(this owner's facts) in the rare case, versus returning nothing at all.
    /// </para>
    /// </remarks>
    public static string SearchByVectorOwnerScopedFallback(bool includeShared, bool currentValidTime = false)
    {
        var owner = includeShared
            ? "(f.owner_id = $ownerId OR f.owner_id IS NULL)"
            : "f.owner_id = $ownerId";
        // The SECOND live fact path, and the one every prior analysis of this gap predates -- it did not
        // exist before 1.4.1. Gating only the indexed query above would leave valid time silently
        // bypassed for exactly the starved multi-tenant owners this fallback was added to rescue.
        var validTime = currentValidTime
            ? @"
              AND (f.valid_from  IS NULL OR f.valid_from  <= datetime($now))
              AND (f.valid_until IS NULL OR f.valid_until >  datetime($now))"
            : string.Empty;
        return $@"
            MATCH (f:Fact)
            WHERE {owner}
              AND f.embedding IS NOT NULL
              AND f.invalidated_at IS NULL{validTime}
            WITH f, vector.similarity.cosine(f.embedding, $embedding) AS score
            WHERE score >= $minScore
            RETURN f AS node, score
            ORDER BY score DESC
            LIMIT $limit";
    }

    // ── CreateExtractedFromRelationshipAsync ────────────────────────────

    /// <summary>Link a Fact to a Message via EXTRACTED_FROM.</summary>
    public const string CreateExtractedFrom = @"
                MATCH (f:Fact {id: $factId}), (m:Message {id: $messageId})
                MERGE (f)-[:EXTRACTED_FROM]->(m)";

    // ── CreateAboutRelationshipAsync ───────────────────────────────────

    /// <summary>Link a Fact to an Entity via ABOUT.</summary>
    public const string CreateAbout = @"
                MATCH (f:Fact {id: $factId}), (e:Entity {id: $entityId})
                MERGE (f)-[:ABOUT]->(e)";

    // ── CreateConversationFactRelationshipAsync ─────────────────────────

    /// <summary>Link a Conversation to a Fact via HAS_FACT.</summary>
    public const string CreateConversationFact = @"
                MATCH (c:Conversation {id: $conversationId}), (f:Fact {id: $factId})
                MERGE (c)-[:HAS_FACT]->(f)";

    // ── GetPageWithoutEmbeddingAsync ────────────────────────────────────

    /// <summary>Get facts that have no embedding yet (for background embedding jobs).</summary>
    public const string GetPageWithoutEmbedding =
        "MATCH (f:Fact) WHERE f.embedding IS NULL RETURN f LIMIT $limit";

    // ── UpdateEmbeddingAsync ───────────────────────────────────────────

    /// <summary>Update embedding for a single fact.</summary>
    public const string UpdateEmbedding =
        "MATCH (f:Fact {id: $id}) SET f.embedding = $embedding";

    // ── DeleteAsync ────────────────────────────────────────────────────

    /// <summary>
    /// Detach-delete a fact by id and report whether it existed. When scoped (R1) the delete only affects
    /// the owner's own facts — never another owner's, and never shared/global ones. Null ⇒ unscoped.
    /// </summary>
    public static string Delete(bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND f.owner_id = $ownerId" : string.Empty;
        return @"
            MATCH (f:Fact {id: $factId})
            WHERE true" + owner + @"
            DETACH DELETE f
            RETURN count(f) > 0 AS deleted";
    }

    // ── InvalidateAsync (D5 — transaction clock) ───────────────────────

    /// <summary>
    /// Soft-invalidate a fact by id: stamp <c>invalidated_at</c> so it drops out of live recall but is
    /// kept — auditable, recoverable, and still visible to as-of recall for times before invalidation.
    /// Owner-scoped (R1): when set, only the owner's own fact is invalidated, never another owner's,
    /// never shared/global. Idempotent — <c>coalesce</c> preserves the first invalidation time.
    /// </summary>
    public static string Invalidate(bool hasOwnerFilter)
    {
        var owner = hasOwnerFilter ? " AND f.owner_id = $ownerId" : string.Empty;
        return @"
            MATCH (f:Fact {id: $id})
            WHERE true" + owner + @"
            SET f.invalidated_at = coalesce(f.invalidated_at, datetime($now))
            RETURN count(f) > 0 AS invalidated";
    }

    // ── SupersedeAsync (D7 — contradiction → supersession) ─────────────

    /// <summary>
    /// Supersede a loser fact with a winner: close the loser non-destructively (stamp both
    /// <c>invalidated_at</c> — transaction clock, drops it from live recall — and <c>valid_until</c> —
    /// valid-time clock, closes its real-world window) and link <c>(loser)-[:SUPERSEDED_BY]-&gt;(winner)</c>.
    /// Nothing is deleted: the loser stays visible to as-of recall for times before supersession.
    /// Owner-scoped (R1): when set, <b>both</b> facts must belong to the owner — a scoped supersede can
    /// neither read nor mutate another owner's facts. Idempotent — <c>coalesce</c> preserves the first
    /// timestamps and <c>MERGE</c> keeps the edge unique.
    /// </summary>
    public static string Supersede(bool hasOwnerFilter)
    {
        var loserOwner = hasOwnerFilter ? " AND loser.owner_id = $ownerId" : string.Empty;
        var winnerOwner = hasOwnerFilter ? " AND winner.owner_id = $ownerId" : string.Empty;
        // Same-owner guard (R1): loser and winner must belong to the same owner — even on the unscoped
        // (admin) path — so a cross-owner :SUPERSEDED_BY link can never be created. `loser <> winner`
        // rejects a self-supersede (same id), which would otherwise invalidate a live node and create a
        // :SUPERSEDED_BY self-loop while reporting success.
        return @"
            MATCH (loser:Fact {id: $loserId})
            WHERE true" + loserOwner + @"
            MATCH (winner:Fact {id: $winnerId})
            WHERE true" + winnerOwner + @"
              AND coalesce(loser.owner_id, '*') = coalesce(winner.owner_id, '*')
              AND loser <> winner
            SET loser.invalidated_at = coalesce(loser.invalidated_at, datetime($now)),
                loser.valid_until    = coalesce(loser.valid_until, datetime($now))
            MERGE (loser)-[:SUPERSEDED_BY]->(winner)
            RETURN count(loser) > 0 AS superseded";
    }

    // ── FindByTripleAsync ──────────────────────────────────────────────

    /// <summary>
    /// Looks a fact up by the same canonical triple the write path MERGEs on, with an optional
    /// owner/shared filter (R1) so a lookup cannot reach into another owner's private facts.
    /// </summary>
    /// <remarks>
    /// <b>This used to ask a different question than the write path answers.</b> It matched
    /// <c>toLower(f.subject) = toLower($subject)</c> on all three properties, while
    /// <see cref="UpsertBatch"/> and <c>FusedPersistenceQueries.FactUpsertBatch</c> MERGE on
    /// <c>{subject_key, predicate_key, object_key, owner_key}</c>.
    /// <para>
    /// Those are not the same predicate. <c>MemoryTripleCanonicalizer.CanonicalValue</c> applies
    /// <c>ToLowerInvariant</c> <i>and collapses whitespace runs</i>; Cypher's <c>toLower</c> does
    /// neither, and the two disagree outright on U+0130 — the documented reason these keys are
    /// computed in C# and never in Cypher. So this lookup could find a <b>different fact than a
    /// MERGE would collapse onto</b>, which is a correctness bug independent of any performance
    /// concern.
    /// </para>
    /// <para>
    /// It is also the only shape that can use an index. Measured on 5.26 with 20,000 facts: the
    /// <c>toLower</c> form plans a <c>NodeByLabelScan</c>; filtering all four canonical keys plans a
    /// <c>NodeIndexSeek</c> returning one row. <b>Three of the four columns is not enough</b> —
    /// <c>fact_merge_key_idx</c> requires every column filtered, not merely a leading prefix, which
    /// is why the owner clause here matches <c>owner_key</c> rather than <c>owner_id</c>.
    /// </para>
    /// <para>
    /// <b>Unscoped lookups still scan</b>, and deliberately so: with no owner there is no fourth
    /// column to filter. They keep the correctness fix and lose three per-row <c>toLower</c> calls,
    /// but no seek is claimed for them.
    /// </para>
    /// </remarks>
    public static string FindByTriple(bool hasOwnerFilter, bool includeShared)
    {
        // owner_key, not owner_id: only owner_key is part of the merge-key index, and only a filter
        // on every one of its columns produces a seek. Shared facts carry the shared marker as their
        // owner_key, so include-shared admits both rather than testing for a null owner.
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND f.owner_key IN [$ownerKey, $sharedOwnerKey]"
                            : " AND f.owner_key = $ownerKey";
        return $@"
            MATCH (f:Fact)
            WHERE f.subject_key = $subjectKey
              AND f.predicate_key = $predicateKey
              AND f.object_key = $objectKey{owner}
            RETURN f LIMIT 1";
    }
}
