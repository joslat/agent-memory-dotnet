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
                f.metadata           = $metadata,
                // R7 salience. Counts how often THE WORLD asserted this fact, incremented only here
                // and in ON MATCH -- i.e. once per ingestion that re-states the same triple.
                // Deliberately NOT the :MemoryReadAudit trail, which counts how often WE surfaced it:
                // ranking on our own retrievals is a rich-get-richer loop that reinforces whatever
                // already ranks highly and calls it learning.
                f.mention_count      = 1
            ON MATCH SET
                f.category           = $category,
                // S2 corroboration. A re-asserted triple is the world saying it again, so confidence
                // EARNS rather than being replaced by whatever the latest extraction happened to
                // report. Gated by the parameter itself -- at alpha 0 this is the original assignment,
                // byte for byte, so nothing recorded before S2 moves.
                f.confidence         = CASE WHEN $reinforceAlpha > 0
                    THEN CASE WHEN coalesce(f.confidence, $confidence) + $reinforceAlpha > 1.0
                        THEN 1.0
                        ELSE coalesce(f.confidence, $confidence) + $reinforceAlpha END
                    ELSE $confidence END,
                f.valid_from         = CASE WHEN $validFrom IS NOT NULL THEN datetime($validFrom) ELSE f.valid_from END,
                f.valid_until        = CASE WHEN $validUntil IS NOT NULL THEN datetime($validUntil) ELSE f.valid_until END,
                f.source_message_ids = $sourceMessageIds,
                f.updated_at         = datetime($updatedAtUtc),
                f.metadata           = $metadata,
                f.invalidated_at     = null,
                f.mention_count      = coalesce(f.mention_count, 1) + 1
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
                f.metadata           = item.metadata,
                f.mention_count      = 1
            ON MATCH SET
                f.category           = item.category,
                // S2 corroboration, identical to the single-item path. A counter that depended on
                // which write path ran would make reinforcement a property of batching rather than of
                // the conversation.
                f.confidence         = CASE WHEN $reinforceAlpha > 0
                    THEN CASE WHEN coalesce(f.confidence, item.confidence) + $reinforceAlpha > 1.0
                        THEN 1.0
                        ELSE coalesce(f.confidence, item.confidence) + $reinforceAlpha END
                    ELSE item.confidence END,
                f.valid_from         = CASE WHEN item.valid_from IS NOT NULL THEN datetime(item.valid_from) ELSE f.valid_from END,
                f.valid_until        = CASE WHEN item.valid_until IS NOT NULL THEN datetime(item.valid_until) ELSE f.valid_until END,
                f.source_message_ids = item.source_message_ids,
                f.updated_at         = datetime(item.updated_at),
                f.metadata           = item.metadata,
                f.invalidated_at     = null,
                f.mention_count      = coalesce(f.mention_count, 1) + 1
            RETURN f";

    // ── R7 mention frequency ───────────────────────────────────────────

    /// <summary>
    /// How often the world asserted each of these facts, for the whole candidate set at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>mention_count</c> is incremented only by the upsert paths, i.e. once per ingestion that
    /// re-states the same triple. It is a <b>salience</b> signal: a fact the conversation keeps
    /// returning to matters more than one mentioned in passing.
    /// </para>
    /// <para>
    /// <b>Explicitly not the read audit.</b> <c>:MemoryReadAudit</c> records how often <i>we</i>
    /// surfaced a fact, and ranking on that is a rich-get-richer loop: whatever ranks highly gets
    /// retrieved, which raises its count, which raises its rank. It would look like learning and be
    /// self-reinforcement.
    /// </para>
    /// <para>
    /// Facts written before this property existed report <c>1</c> via <c>coalesce</c> — asserted once,
    /// which is what a single ingestion means and the only honest reading of an absent counter.
    /// </para>
    /// </remarks>
    public const string MentionCounts = @"
            MATCH (f:Fact)
            WHERE f.id IN $candidateIds
            RETURN f.id AS candidateId, coalesce(f.mention_count, 1) AS mentions";

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

    /// <summary>Reinforce an existing fact reached by dedup: bump its confidence and count the mention.</summary>
    /// <remarks>
    /// <para>
    /// <b><c>mention_count</c> is incremented here for the same reason it is in <see cref="Upsert"/> and
    /// <see cref="UpsertBatch"/>:</b> arriving by dedup still means the world asserted this fact again,
    /// only in different words. This statement previously set confidence alone, which made the counter
    /// depend on which write path ran — exactly what <see cref="UpsertBatch"/>'s comment says it must
    /// not do.
    /// </para>
    /// <para>
    /// The consequence was not cosmetic. <c>LongTerm.DeduplicateOnCreate</c> defaults to <c>true</c>, and
    /// a re-asserted triple — including a byte-identical one, which trivially clears the similarity
    /// threshold — reaches this statement instead of the triple MERGE. So on the single-add API the
    /// counter never left 1, the working-memory tier (which admits at <c>MinFactMentionCount</c>,
    /// default 2) could never admit a directly-added fact however often it was re-asserted, and
    /// <c>MentionFrequencyReranker</c> had no salience signal to rank on.
    /// </para>
    /// </remarks>
    public const string MarkDeduplicated = @"
            MATCH (f:Fact {id: $id})
            SET f.confidence    = $confidence,
                f.mention_count = coalesce(f.mention_count, 1) + 1
            RETURN f";

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
        bool hasOwnerFilter, bool includeShared, int topK, bool recencyRerank = false,
        bool currentValidTime = false, bool omitEmbedding = false) =>
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
            recencyRerank, omitEmbedding);

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
            SET f.invalidated_at = coalesce(f.invalidated_at, datetime($now))"
            // 30.6. Any aggregate computed from this fact stops being live in the SAME statement. A
            // derived 750 whose input 800 was just retracted is a manufactured confident-wrong answer,
            // and an eventually-consistent sweep would leave a window in which exactly that is
            // retrievable. Unconditional rather than flag-gated: switching the accountant off must not
            // freeze every aggregate it ever wrote into permanent truth. Row-neutral on a store with no
            // derived facts (guard G1) -- see DerivedFactQueries.CascadeInvalidateDerived.
            + DerivedFactQueries.CascadeInvalidateDerived("f") + @"
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
                loser.valid_until    = coalesce(loser.valid_until, datetime($now)),
                // S2 contradiction. Twice the corroboration step, and downward: being contradicted is
                // stronger evidence against a fact than one more restatement is for it. Floored at 0
                // rather than allowed negative -- confidence is a [0,1] quantity every ranking and
                // dedup computation reads, and a negative would propagate somewhere it means nothing.
                loser.confidence     = CASE WHEN $reinforceAlpha > 0
                    THEN CASE WHEN coalesce(loser.confidence, 0.0) - (2 * $reinforceAlpha) < 0.0
                        THEN 0.0
                        ELSE coalesce(loser.confidence, 0.0) - (2 * $reinforceAlpha) END
                    ELSE loser.confidence END
            MERGE (loser)-[:SUPERSEDED_BY]->(winner)"
            // 30.6, same reasoning as Invalidate: an aggregate over a fact that was just replaced is
            // stale the instant the replacement lands. The next accountant pass recomputes and re-arms
            // it from the surviving inputs.
            + DerivedFactQueries.CascadeInvalidateDerived("loser") + @"
            RETURN count(loser) > 0 AS superseded";
    }

    // ── Legible forgetting (30.8) ──────────────────────────────────────

    /// <summary>
    /// Vector search over facts the prune let go of — <c>invalidated_reason = 'decay'</c> only.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A sibling of <see cref="SearchByVector"/> with exactly two clauses inverted, so the two cannot
    /// drift apart on anything else. The vector index already contains these nodes: soft-invalidation
    /// keeps the embedding, and every live query simply filters them out afterwards. This inverts that
    /// filter rather than adding an index.
    /// </para>
    /// <para>
    /// <b><c>invalidated_reason = 'decay'</c>, not merely <c>invalidated_at IS NOT NULL</c>.</b> A
    /// superseded fact is also invalidated, and reporting one as forgotten would be a lie in the most
    /// damaging direction: the system did not forget it, it <i>replaced</i> it, and its replacement is
    /// live and should be answering the question. The reason property is the partition.
    /// </para>
    /// <para>
    /// <b>The same <c>$minScore</c> floor a live fact would face.</b> A tombstone that clears a looser
    /// bar is a confident statement about having forgotten something on an unrelated topic — worse
    /// than silence, because it invites the user to re-supply information they never gave.
    /// </para>
    /// <para>
    /// <b>No escalation tiers.</b> The owner-starvation fallback ladder is deliberately not replicated
    /// for a diagnostic line: if the global top-K starves, the tombstone silently does not render,
    /// which is the correct failure direction for a surface whose whole job is honesty about absence.
    /// </para>
    /// </remarks>
    public static string SearchDecayedByVector(bool hasOwnerFilter, bool includeShared, int topK) =>
        VectorRerank.Finish(
            new CypherBuilder()
                .WithVectorSearch("fact_embedding_idx", "$embedding", "node", topK)
                .Where("score >= $minScore")
                .And("node.invalidated_at IS NOT NULL")
                .And("node.invalidated_reason = 'decay'")
                .And(includeShared ? "(node.owner_id = $ownerId OR node.owner_id IS NULL)" : "node.owner_id = $ownerId", when: hasOwnerFilter),
            recencyRerank: false, omitEmbedding: true);

    // ── Prospective firing (30.7) ──────────────────────────────────────

    /// <summary>
    /// Facts whose validity <b>opened</b> in <c>(since, now]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No <c>$embedding</c>, no <c>$minScore</c>, no vector index.</b> That absence is the
    /// specification, not an omission: a reminder is off-topic by definition, so a similarity-scoped
    /// version of this query could never surface the reminders that matter most.
    /// </para>
    /// <para>
    /// <c>valid_from</c>, the valid-time clock — deliberately not <c>created_at</c>. "I learned last
    /// week that the renewal is today" must fire today, not last week, and those are different clocks
    /// answering different questions.
    /// </para>
    /// <para>
    /// Ordered most-recently-due first, so <c>LIMIT</c> truncates the oldest. Half-open on the same
    /// convention every other window query here uses.
    /// </para>
    /// </remarks>
    public static string GetDueFacts(bool hasOwnerFilter, bool includeShared) => @"
            MATCH (f:Fact)
            WHERE f.invalidated_at IS NULL
              AND f.valid_from IS NOT NULL
              AND f.valid_from > datetime($since)
              AND f.valid_from <= datetime($now)"
        + DeltaOwner(hasOwnerFilter, includeShared) + @"
            RETURN f ORDER BY f.valid_from DESC LIMIT $limit";

    /// <summary>
    /// Facts whose validity <b>closes</b> between now and the expiry horizon.
    /// </summary>
    /// <remarks>
    /// <c>valid_until &gt; $now</c> excludes what has already expired: that belongs to delta recall's
    /// expired-validity bucket, and reporting it here as "expiring" would be a tense error the reader
    /// acts on. The horizon is computed in C# and passed ISO-8601 — no Cypher duration arithmetic, the
    /// same way <c>$now</c> already travels.
    /// </remarks>
    public static string GetExpiringFacts(bool hasOwnerFilter, bool includeShared) => @"
            MATCH (f:Fact)
            WHERE f.invalidated_at IS NULL
              AND f.valid_until IS NOT NULL
              AND f.valid_until > datetime($now)
              AND f.valid_until <= datetime($expiryHorizon)"
        + DeltaOwner(hasOwnerFilter, includeShared) + @"
            RETURN f ORDER BY f.valid_until ASC LIMIT $limit";

    // ── Delta recall (30.5) ────────────────────────────────────────────

    /// <summary>
    /// The owner fragment shared by every delta query. Same shape as <c>GetBySubject</c>'s.
    /// </summary>
    private static string DeltaOwner(bool hasOwnerFilter, bool includeShared, string alias = "f") =>
        !hasOwnerFilter ? string.Empty
            : includeShared ? $" AND ({alias}.owner_id = $ownerId OR {alias}.owner_id IS NULL)"
                            : $" AND {alias}.owner_id = $ownerId";

    /// <summary>
    /// Facts the system newly knows in <c>(since, until]</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Transaction clock, deliberately.</b> Not <c>valid_from</c> — a fact learned yesterday about
    /// 2019 would never surface. Not <c>updated_at</c> — <c>Upsert ON MATCH</c> bumps it on every
    /// restatement, so restatements would replay as "new" forever.
    /// </para>
    /// <para>
    /// This is also what makes a backfill behave: an import stamps <c>created_at</c> = import time, so
    /// imported facts appear as new exactly once, in the first delta after the import, which is the
    /// honest answer — the system newly knows them.
    /// </para>
    /// <para>
    /// Boundary convention, applied without exception across every delta query: strictly
    /// <c>&gt; $since</c>, inclusively <c>&lt;= $until</c>. One <c>&gt;=</c> where <c>&gt;</c> belongs
    /// silently duplicates an item across consecutive windows.
    /// </para>
    /// </remarks>
    public static string DeltaNewFacts(bool hasOwnerFilter, bool includeShared) => @"
            MATCH (f:Fact)
            WHERE f.created_at > datetime($since) AND f.created_at <= datetime($until)
              AND f.invalidated_at IS NULL" + DeltaOwner(hasOwnerFilter, includeShared) + @"
            RETURN f ORDER BY f.created_at ASC LIMIT $limit";

    /// <summary>Facts replaced in the window, with their successor.</summary>
    public static string DeltaSupersededPairs(bool hasOwnerFilter, bool includeShared) => @"
            MATCH (old:Fact)-[:SUPERSEDED_BY]->(new:Fact)
            WHERE old.invalidated_at > datetime($since) AND old.invalidated_at <= datetime($until)"
        + DeltaOwner(hasOwnerFilter, includeShared, "old") + @"
            RETURN old, new ORDER BY old.invalidated_at ASC LIMIT $limit";

    /// <summary>Facts closed in the window with no successor — retracted rather than replaced.</summary>
    public static string DeltaInvalidatedFacts(bool hasOwnerFilter, bool includeShared) => @"
            MATCH (f:Fact)
            WHERE f.invalidated_at > datetime($since) AND f.invalidated_at <= datetime($until)
              AND NOT (f)-[:SUPERSEDED_BY]->(:Fact)" + DeltaOwner(hasOwnerFilter, includeShared) + @"
            RETURN f ORDER BY f.invalidated_at ASC LIMIT $limit";

    /// <summary>
    /// Facts whose real-world window closed in the window and which are still live on the transaction clock.
    /// </summary>
    /// <remarks>
    /// The <c>invalidated_at IS NULL</c> gate is load-bearing: <c>Supersede</c> stamps
    /// <b>both</b> clocks, so without it a superseded fact would appear twice — once as a pair and
    /// once here — and the exactly-once invariant the whole feature rests on would be false.
    /// </remarks>
    public static string DeltaExpiredValidity(bool hasOwnerFilter, bool includeShared) => @"
            MATCH (f:Fact)
            WHERE f.valid_until IS NOT NULL
              AND f.valid_until > datetime($since) AND f.valid_until <= datetime($until)
              AND f.invalidated_at IS NULL" + DeltaOwner(hasOwnerFilter, includeShared) + @"
            RETURN f ORDER BY f.valid_until ASC LIMIT $limit";

    /// <summary>
    /// Facts that became true during the window, having been known before it.
    /// </summary>
    /// <remarks>
    /// <c>created_at &lt;= $since</c> is what keeps this disjoint from <c>NewFacts</c>: a fact both
    /// learned and becoming due inside one window belongs in exactly one bucket, and "new" is the more
    /// informative of the two.
    /// </remarks>
    public static string DeltaNewlyDueProspective(bool hasOwnerFilter, bool includeShared) => @"
            MATCH (f:Fact)
            WHERE f.valid_from IS NOT NULL
              AND f.valid_from > datetime($since) AND f.valid_from <= datetime($until)
              AND f.created_at <= datetime($since)
              AND f.invalidated_at IS NULL
              AND (f.valid_until IS NULL OR f.valid_until > datetime($until))"
        + DeltaOwner(hasOwnerFilter, includeShared) + @"
            RETURN f ORDER BY f.valid_from ASC LIMIT $limit";

    // ── GetSupersessionPredecessorsAsync (30.2 projection) ─────────────

    /// <summary>
    /// For a set of fact ids the caller already holds, what each one superseded — newest first, capped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>No owner clause, and that is a conclusion rather than an omission.</b> The edge this walks is
    /// only ever created by <see cref="Supersede"/>, which carries a same-owner guard on <i>both</i>
    /// ends — even on the unscoped admin path — so a <c>:SUPERSEDED_BY</c> chain cannot cross owners
    /// and cannot be made to. Anchoring on ids the caller already retrieved under their own scope means
    /// this query cannot widen it. An owner-isolation test covers the claim anyway.
    /// </para>
    /// <para>
    /// One call per recall, not one per fact: <c>cur.id IN $factIds</c> batches the whole section. The
    /// per-fact cap is applied inside the collect so a long chain cannot blow up the payload.
    /// </para>
    /// <para>
    /// (A const, so it IS in the Cypher snapshot inventory — it is a fixed query with no variants.)
    /// </para>
    /// </remarks>
    public const string GetSupersessionPredecessors = @"
            MATCH (prev:Fact)-[:SUPERSEDED_BY]->(cur:Fact)
            WHERE cur.id IN $factIds
            WITH cur, prev
            ORDER BY coalesce(prev.valid_until, prev.invalidated_at) DESC
            WITH cur, collect({
                object: prev.object,
                invalidated_at: prev.invalidated_at,
                valid_until: prev.valid_until
            })[0..$maxChain] AS chain
            RETURN cur.id AS factId, chain";

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
