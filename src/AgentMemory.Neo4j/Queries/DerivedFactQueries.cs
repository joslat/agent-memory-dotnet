namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Cypher for the session accountant (30.6): reading a group, and writing what it computed.
/// </summary>
/// <remarks>
/// A derived fact is an ordinary <c>:Fact</c> node carrying <c>fact_kind='derived'</c>. That choice is what
/// buys the whole feature its recall path for free — the existing vector index, budget, owner scoping,
/// <c>invalidated_at</c> gate and valid-time gate all apply with no changes at all. A
/// <c>:DerivedFact</c> label would have cost a parity allowlist entry <i>and</i> forfeited every one of
/// those, because every fact query matches <c>:Fact</c>.
/// </remarks>
internal static class DerivedFactQueries
{
    /// <summary>
    /// The live, non-derived facts of one <c>(subject, predicate, owner)</c> group, in the order they
    /// became true.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Ordered by <c>coalesce(valid_from, created_at)</c>, ascending.</b> The order is the
    /// arithmetic: a delta over an unordered group subtracts two arbitrary members and reports the
    /// result as a change. Valid time comes first so a fact learned yesterday about 2019 sorts as 2019;
    /// the fallback matters as much, because most extracted facts carry no valid time at all and
    /// dropping them would leave every group too small to aggregate.
    /// </para>
    /// <para>
    /// <b><c>fact_kind &lt;&gt; 'derived'</c> keeps the DAG one level deep.</b> Aggregating aggregates would
    /// make the invalidation cascade recursive, and a cascade that has to walk an arbitrary-depth chain
    /// inside a supersede statement is a cascade that will eventually be made asynchronous "for
    /// performance" — at which point stale derived values become reachable.
    /// </para>
    /// </remarks>
    public static string GetGroupFacts(bool hasOwnerFilter, bool includeShared)
    {
        var owner = !hasOwnerFilter ? string.Empty
            : includeShared ? " AND (f.owner_id = $ownerId OR f.owner_id IS NULL)"
                            : " AND f.owner_id = $ownerId";
        return @"
            MATCH (f:Fact)
            WHERE f.subject_key = $subjectKey
              AND f.predicate_key = $predicateKey
              AND f.invalidated_at IS NULL
              AND coalesce(f.fact_kind, '') <> 'derived'" + owner + @"
            RETURN f
            ORDER BY coalesce(f.valid_from, f.created_at) ASC, f.id ASC
            LIMIT $limit";
    }

    /// <summary>
    /// Writes or refreshes one derived fact and repoints its <c>DERIVED_FROM</c> edges.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Identity is <c>derivation_key</c>, not the four-key triple index every other fact uses.</b>
    /// The value changes on every recompute — that is what an aggregate does — so triple identity would
    /// spawn a fresh node per value and the graph would accumulate one dead aggregate per observation.
    /// The key is a SHA-256 of <c>subject_key|predicate_key|operator|owner_key</c>, computed in C#
    /// rather than in Cypher, per the U+0130 lesson: Cypher's <c>toLower</c> and .NET's do not agree on
    /// every input, and a key computed in two places will eventually be computed two ways.
    /// </para>
    /// <para>
    /// <b><c>invalidated_at = null</c> re-arms.</b> A previously cascaded-out aggregate whose group
    /// became live again comes back rather than staying dead, which is why the cascade can afford to be
    /// blunt: it invalidates on any input change and lets the next accountant pass restore what
    /// survives.
    /// </para>
    /// <para>
    /// Old edges are deleted before new ones are merged, so an input that left the group stops being
    /// cited as provenance. A derived value whose stated inputs no longer include the fact it was
    /// actually computed from is worse than one with no provenance at all.
    /// </para>
    /// <para>
    /// <b>Guard G2, structurally: a derived fact carries no merge-key quadruple at all.</b> The write
    /// path MERGEs extracted facts on
    /// <c>{subject_key, predicate_key, object_key, owner_key}</c> and <c>FindByTriple</c> looks them up
    /// the same way. A derived node carrying those four properties could be matched by either — so a
    /// user restating a number would silently merge <i>into</i> an aggregate, overwriting its value
    /// while leaving its <c>DERIVED_FROM</c> edges and derivation string in place: a fact wearing
    /// provenance for arithmetic that never produced it. Omitting the properties makes that
    /// unreachable rather than unlikely, because MERGE and the lookup both require a non-null match on
    /// every column. Nothing is lost: the group read filters derived nodes out by design, recall reaches
    /// them by vector, and isolation reads <c>owner_id</c>, which is still set.
    /// </para>
    /// </remarks>
    public const string UpsertDerived = @"
            MERGE (f:Fact {derivation_key: $derivationKey})
            ON CREATE SET f.id = $id, f.created_at = datetime($now), f.mention_count = 1
            SET f.fact_kind = 'derived',
                f.subject = $subject, f.predicate = $predicate, f.object = $object,
                f.owner_id = $ownerId,
                f.confidence = $confidence, f.embedding = $embedding,
                f.derivation_operator = $operator, f.derivation = $derivation,
                f.derived_at = datetime($now), f.updated_at = datetime($now),
                f.invalidated_at = null, f.metadata = $metadata
            WITH f
            OPTIONAL MATCH (f)-[old:DERIVED_FROM]->(:Fact)
            DELETE old
            WITH DISTINCT f
            UNWIND $inputFactIds AS inputId
              MATCH (i:Fact {id: inputId})
              WHERE coalesce(i.fact_kind, '') <> 'derived'
              MERGE (f)-[:DERIVED_FROM]->(i)
            RETURN f";

    /// <summary>
    /// The cascade appended to <c>Supersede</c> and <c>Invalidate</c>: any aggregate computed from a
    /// fact that just stopped being live stops being live too, <b>in the same statement</b>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The safety property of the whole feature.</b> A derived <c>750</c> whose input <c>800</c> was
    /// superseded is a manufactured confident-wrong answer — stored, embedded, recallable, and carrying
    /// inline provenance that makes it look verified. An eventually-consistent sweep would leave a
    /// window in which exactly that is retrievable, so this is same-statement or it is nothing.
    /// </para>
    /// <para>
    /// <b>Unconditional, not flag-gated.</b> If the feature is switched off while derived facts exist,
    /// staleness protection has to survive the flag — otherwise turning the accountant off would
    /// silently freeze every aggregate it ever wrote into permanent truth.
    /// </para>
    /// <para>
    /// <b>Cardinality-safe (guard G1).</b> <c>OPTIONAL MATCH</c> binds nothing on a store with no
    /// derived facts, and the <c>SET</c> then applies to a null row — a no-op that produces no extra
    /// rows, so the surrounding statement's <c>count()</c> and <c>RETURN</c> are unchanged. The
    /// <c>WITH DISTINCT</c> before it is what makes that true: without it, a fact with N derived
    /// dependants would multiply the outer row N times and the caller's "did it work" count would report
    /// N instead of 1.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b><c>internal</c>, not <c>public</c>, and that is load-bearing.</b> This is a <i>fragment</i>
    /// spliced into another statement, not a query — it opens with <c>WITH DISTINCT</c> and depends on
    /// a variable its caller bound. <c>CypherQueryExecutionSweepTests</c> enumerates every
    /// <c>public static</c> string-returning member of this namespace and EXPLAINs the result against a
    /// live database, which a fragment cannot survive. Its coverage is not lost: the sweep EXPLAINs
    /// <c>FactQueries.Supersede</c> and <c>Invalidate</c>, which contain it, and planning it inside its
    /// real statement is a stronger check than planning it alone.
    /// </remarks>
    internal static string CascadeInvalidateDerived(string inputAlias) => $@"
            WITH DISTINCT {inputAlias}
            OPTIONAL MATCH (derived:Fact)-[:DERIVED_FROM]->({inputAlias})
            SET derived.invalidated_at = coalesce(derived.invalidated_at, datetime($now))
            WITH DISTINCT {inputAlias}";
}
