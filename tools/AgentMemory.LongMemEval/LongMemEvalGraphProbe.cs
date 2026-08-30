using Neo4j.Driver;

namespace AgentMemory.LongMemEval;

internal interface ILongMemEvalGraphProbe
{
    /// <summary>
    /// Counts the supersession the run actually wrote: <c>:SUPERSEDED_BY</c> edges, invalidated
    /// facts, and the predicates extraction produced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This replaces an inference that was wrong.</b> The store-state gate used to argue from
    /// facts-placed-per-question falling (8.25 → 7.92) that supersession had fired. Three arms later
    /// the same lever produced 7.65 with no lever change in between, so that signal was extraction
    /// variance and the gate had been certifying an off-state run as "verified ON" — the exact error
    /// the gate existed to prevent, in the gate itself.
    /// </para>
    /// <para>
    /// A count of edges cannot be confused with noise: zero means the mechanism did not run, and the
    /// predicate histogram says why, because write-time supersession is refused outright for any
    /// predicate outside the six the vocabulary declares single-valued.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <para>
    /// Defaulted to empty because these three are OPTIONAL DIAGNOSTICS, not part of what a probe must
    /// be able to do. Making them required broke six unrelated test fakes that have no graph to
    /// report on, which is a signal about the contract rather than about the fakes: a store-shape
    /// reading is something a probe MAY offer, and empty is the honest answer when it cannot.
    /// </para>
    /// <para>
    /// <b>EMPTY MEANS "NO STORE TO READ", NOT "MEASURED ZERO", and a reader must not collapse the
    /// two.</b> A default that silently satisfies every caller is how a constant column is born — a
    /// value that could not have come out any other way, read as though it were evidence. That has
    /// already cost this project once: "12 of 12 pairs with zero entity links" was reported as a
    /// finding when every fact in every store had zero, because nothing writes those links. A caller
    /// that needs to distinguish "the mechanism wrote nothing" from "nobody looked" must check
    /// whether a real probe was supplied, exactly as the render gate treats a null block as a
    /// failure rather than a pass.
    /// </para>
    /// </remarks>
    Task<LongMemEvalSupersessionStore> ReadSupersessionStoreAsync(CancellationToken cancellationToken)
        => Task.FromResult(new LongMemEvalSupersessionStore(0, 0, 0, []));

    /// <summary>
    /// Classifies what actually sits in fact OBJECTS, to tell "stored at the wrong grain" from
    /// "never captured".
    /// </summary>
    /// <remarks>
    /// The predicate histogram showed extraction writing speech acts across three unrelated corpora.
    /// It could not say whether the content survived inside those triples — an amount attached to
    /// <c>said as much about</c> is a grain problem, an amount nowhere in the store is a capture
    /// problem, and the two need different fixes. Objects are returned verbatim in samples so the
    /// classification is eyeballable rather than trusted.
    /// </remarks>
    Task<LongMemEvalFactObjectShape> ReadFactObjectShapeAsync(CancellationToken cancellationToken)
        => Task.FromResult(new LongMemEvalFactObjectShape(0, 0, 0, 0, [], [], []));

    /// <summary>
    /// Finds subject-predicate pairs carrying more than one distinct object, and reports how those
    /// facts resolve to entities.
    /// </summary>
    /// <remarks>
    /// The condition under which a retrieved amount cannot be attributed: if
    /// <c>payment | has_amount</c> holds three different values, "what did the payment cost" has no
    /// single answer however well retrieval performs. Whether those facts reach distinct entities,
    /// one merged entity, or none separates three different defects with three different fixes.
    /// </remarks>
    Task<LongMemEvalSubjectAmbiguity> ReadSubjectAmbiguityAsync(CancellationToken cancellationToken)
        => Task.FromResult(new LongMemEvalSubjectAmbiguity([]));

    Task<LongMemEvalGraphSnapshot> ReadAsync(
        string ownerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// G3B.5. Whether the cold build actually learned anything from the sessions that hold the
    /// answer, checked before any evaluation call is spent on the graph.
    /// </summary>
    /// <remarks>
    /// The existing read-back proves the graph is <i>sound</i> — non-empty, fully provenanced, and
    /// bit-identical to the sealed snapshot. It cannot prove it is <i>adequate</i>: three facts
    /// learned from 474 sessions would pass every current guard. This asks the question that decides
    /// whether Structured mode can work at all, and separates "extraction lost the fact" from
    /// "retrieval missed it" — a distinction BUG-E1 left unattributable.
    /// </remarks>
    /// <remarks>
    /// Defaults to <see langword="null"/> meaning <b>not measured</b> — deliberately not "fine". A
    /// probe that cannot answer must not be able to assert coverage it never checked, so the absent
    /// case falls through to the pre-existing verdicts rather than silently reporting a clean build.
    /// </remarks>
    /// <summary>
    /// The owner's stored memory as plain text, so presence of an answer can be checked by
    /// <b>content</b> rather than by provenance.
    /// </summary>
    /// <remarks>
    /// This interface already promised to separate <i>"extraction lost the fact"</i> from
    /// <i>"retrieval missed it"</i>, and <see cref="ReadGoldCoverageAsync"/> was meant to deliver it.
    /// It does not: it counts <c>EXTRACTED_FROM</c> edges, and those turned out to be batch-level —
    /// a fact links to a mean of 12 messages — so the coverage number cannot fail regardless of what
    /// was actually learned. The intent was right; the implementation measured the wrong thing.
    /// <para>
    /// Reading the text itself is the version that can fail. If the answer's distinctive tokens
    /// appear nowhere in the owner's memory, the question is unanswerable from memory in principle
    /// and every retrieval metric on it is measuring noise.
    /// </para>
    /// <para>
    /// Defaults to empty, meaning <b>not probed</b>. Callers must treat that as "not measured",
    /// never as "nothing stored" — the same convention the rest of this interface follows.
    /// </para>
    /// </remarks>
    Task<IReadOnlyList<string>> ReadMemoryTextAsync(
        string ownerId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    Task<LongMemEvalGoldEvidenceCoverage?> ReadGoldCoverageAsync(
        string ownerId,
        IReadOnlyList<string> goldSourceMessageIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LongMemEvalGoldEvidenceCoverage?>(null);

    /// <summary>
    /// L2. How many live facts the owner's graph holds under each given stored predicate key.
    /// </summary>
    /// <remarks>
    /// The denominator of relation completeness, and the reason Phase L exists. It is a
    /// deterministic <c>count()</c> over the graph, so unlike an accuracy score it is not subject to
    /// answer-model or judge non-determinism: if an extraction change stops learning a relation the
    /// questions need, this number drops and says so. That is the extraction-quality signal the
    /// deterministic fixture (pinned at 1.000 by construction) and the LongMemEval channel
    /// (sd 9.3 cold-build) both fail to provide.
    /// </remarks>
    /// <remarks>
    /// Defaults to <see langword="null"/> meaning <b>not measured</b>, matching
    /// <see cref="ReadGoldCoverageAsync"/>. A probe that cannot answer must not be able to assert a
    /// completeness it never checked. An empty <paramref name="predicateKeys"/> returns an empty
    /// dictionary instead — "nothing to count" is a measured answer, not an absent one.
    /// </remarks>
    Task<IReadOnlyDictionary<string, int>?> ReadRelationFactCountsAsync(
        string ownerId,
        IReadOnlyList<string> predicateKeys,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>?>(null);
}


internal sealed class Neo4jLongMemEvalGraphProbe(IDriver driver) : ILongMemEvalGraphProbe
{

    /// <summary>
    /// Mirrors <c>FactQueries.SearchByCanonicalPredicates</c>' WHERE clause exactly, minus
    /// ORDER BY/LIMIT, so the denominator counts precisely the rows expansion could have returned.
    /// </summary>
    /// <remarks>
    /// Deliberately NOT <c>coalesce(f.predicate_key, toLower(f.predicate))</c>, which the predicate
    /// distribution program uses: a fact whose <c>predicate_key</c> is null is invisible to
    /// expansion, so counting it here would report an unreachable fact as a retrieval miss.
    /// </remarks>
    private const string RelationFactCountQuery =
        """
        MATCH (f:Fact)
        WHERE f.predicate_key IN $predicateKeys
          AND f.invalidated_at IS NULL
          AND (f.owner_id = $ownerId OR f.owner_id IS NULL)
        RETURN f.predicate_key AS predicateKey, count(f) AS factCount
        """;

    // Deliberately three statements rather than one joined query. A single MATCH over facts AND
    // edges returns NO ROW at all when the graph holds zero edges, and zero edges is the single most
    // important thing this can report -- it must arrive as a number, never as an absent row.
    private const string SupersessionEdgeCountQuery =
        """
        MATCH ()-[r:SUPERSEDED_BY]->() RETURN count(r) AS edges
        """;

    private const string FactShapeQuery =
        """
        MATCH (f:Fact)
        RETURN count(f) AS facts,
               count(CASE WHEN f.invalidated_at IS NOT NULL THEN 1 END) AS invalidated
        """;

    private const string PredicateHistogramQuery =
        """
        MATCH (f:Fact)
        RETURN coalesce(f.predicate_key, toLower(f.predicate)) AS predicate, count(f) AS n
        ORDER BY n DESC LIMIT 15
        """;

    private const string FactObjectQuery =
        """
        MATCH (f:Fact)
        RETURN f.subject AS subject, f.predicate AS predicate, f.object AS object
        """;

    // Grouped on the KEYS the store merges on, not on the display strings: two facts differing only
    // by whitespace or case are one fact to the store, and counting them as two would invent
    // ambiguity that retrieval never sees.
    private const string SubjectAmbiguityQuery =
        """
        MATCH (f:Fact)
        WITH f.subject_key AS subjectKey, f.predicate_key AS predicateKey,
             collect(DISTINCT f.object) AS objects, collect(f) AS facts
        WHERE size(objects) > 1
        UNWIND facts AS fact
        OPTIONAL MATCH (fact)-[:ABOUT]->(e:Entity)
        WITH subjectKey, predicateKey, objects, head(collect(fact.subject)) AS subject,
             count(DISTINCT e) AS entities
        RETURN subject, subjectKey, predicateKey, objects, entities
        ORDER BY size(objects) DESC LIMIT 12
        """;

    public async Task<LongMemEvalSubjectAmbiguity> ReadSubjectAmbiguityAsync(
        CancellationToken cancellationToken)
    {
        await using var session = driver.AsyncSession();
        var rows = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(SubjectAmbiguityQuery).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records.Select(r => new LongMemEvalAmbiguousSubject(
                r["subject"].As<string>() ?? string.Empty,
                r["predicateKey"].As<string>() ?? string.Empty,
                r["objects"].As<List<object>>().Select(o => o?.ToString() ?? string.Empty).ToArray(),
                r["entities"].As<int>())).ToArray();
        }).ConfigureAwait(false);

        return new LongMemEvalSubjectAmbiguity(rows);
    }

    public async Task<LongMemEvalFactObjectShape> ReadFactObjectShapeAsync(
        CancellationToken cancellationToken)
    {
        await using var session = driver.AsyncSession();
        var rows = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(FactObjectQuery).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            return records
                .Select(r => (
                    Subject: r["subject"].As<string>() ?? string.Empty,
                    Predicate: r["predicate"].As<string>() ?? string.Empty,
                    Object: r["object"].As<string>() ?? string.Empty))
                .ToArray();
        }).ConfigureAwait(false);

        // The arithmetic corpus answers in the shape `319.97`, so that is what "amount" means here --
        // registered before this was written, and deliberately NOT widened to "any digit", which
        // would count dates and session numbers as money.
        var amount = new System.Text.RegularExpressions.Regex(
            @"(\$\s?\d|\d+\.\d{2})", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var digit = new System.Text.RegularExpressions.Regex(@"\d");

        var amounts = rows.Where(r => amount.IsMatch(r.Object)).ToArray();
        var numeric = rows.Where(r => !amount.IsMatch(r.Object) && digit.IsMatch(r.Object)).ToArray();
        var plain = rows.Where(r => !digit.IsMatch(r.Object)).ToArray();

        static IReadOnlyList<string> Sample((string Subject, string Predicate, string Object)[] rows) =>
            rows.Take(6)
                .Select(r => $"{Trim(r.Subject)} | {Trim(r.Predicate)} | {Trim(r.Object)}")
                .ToArray();

        return new LongMemEvalFactObjectShape(
            rows.Length, amounts.Length, numeric.Length, plain.Length,
            Sample(amounts), Sample(numeric), Sample(plain));
    }

    private static string Trim(string value) =>
        value.Length <= 60 ? value : value[..60] + "…";

    public async Task<LongMemEvalSupersessionStore> ReadSupersessionStoreAsync(
        CancellationToken cancellationToken)
    {
        await using var session = driver.AsyncSession();

        var edges = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(SupersessionEdgeCountQuery).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return record["edges"].As<int>();
        }).ConfigureAwait(false);

        var (facts, invalidated) = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(FactShapeQuery).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return (record["facts"].As<int>(), record["invalidated"].As<int>());
        }).ConfigureAwait(false);

        var predicates = await session.ExecuteReadAsync(async tx =>
        {
            var cursor = await tx.RunAsync(PredicateHistogramQuery).ConfigureAwait(false);
            var rows = await cursor.ToListAsync().ConfigureAwait(false);
            return rows
                .Where(row => row["predicate"] is not null)
                .Select(row => (Predicate: row["predicate"].As<string>(), Count: row["n"].As<int>()))
                .ToArray();
        }).ConfigureAwait(false);

        return new LongMemEvalSupersessionStore(edges, facts, invalidated, predicates);
    }

    public async Task<IReadOnlyDictionary<string, int>?> ReadRelationFactCountsAsync(
        string ownerId,
        IReadOnlyList<string> predicateKeys,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(predicateKeys);
        if (predicateKeys.Count == 0)
            return new Dictionary<string, int>(StringComparer.Ordinal);

        var (records, _, _) = await driver.ExecutableQuery(RelationFactCountQuery)
            .WithParameters(new Dictionary<string, object?>
            {
                ["ownerId"] = ownerId,
                ["predicateKeys"] = predicateKeys.ToList()
            })
            .WithConfig(new QueryConfig(routing: RoutingControl.Readers))
            .ExecuteAsync(cancellationToken)
            .ConfigureAwait(false);

        return records.ToDictionary(
            record => record["predicateKey"].As<string>(),
            record => record["factCount"].As<int>(),
            StringComparer.Ordinal);
    }

    private const string SnapshotQuery =
        """
        CALL {
          MATCH (e:Entity {owner_id: $ownerId})
          RETURN count(e) AS entities
        }
        CALL {
          MATCH (f:Fact {owner_id: $ownerId})
          RETURN count(f) AS facts
        }
        CALL {
          MATCH (p:Preference {owner_id: $ownerId})
          RETURN count(p) AS preferences
        }
        CALL {
          MATCH (t:ReasoningTrace {owner_id: $ownerId})
          RETURN count(t) AS reasoningTraces,
                 count(CASE WHEN t.trace_kind = 'Procedure' THEN 1 END) AS procedures
        }
        CALL {
          MATCH ()-[r:RELATED_TO]->()
          WHERE r.owner_id = $ownerId
          RETURN count(r) AS relationships,
                 count(CASE WHEN size(coalesce(r.source_message_ids, [])) > 0 THEN 1 END)
                   AS relationshipsWithProvenance
        }
        CALL {
          MATCH (n)
          WHERE n.owner_id = $ownerId AND (n:Entity OR n:Fact OR n:Preference)
          OPTIONAL MATCH (n)-[:EXTRACTED_FROM]->(m:Message)
          // A DERIVED fact has no source message and never will: it is computed FROM other facts,
          // and DERIVED_FROM is its provenance. Requiring EXTRACTED_FROM of every learned item
          // encoded an assumption that was true only while derived memory did not exist -- the
          // moment the session accountant creates one, learnedItemsWithProvenance < learnedItems,
          // CompleteProvenance goes false, and the adapter throws on EVERY question. That is what
          // made the first arithmetic-memory ablation void: 50/50 agent errors, ~4.5 hours a run,
          // and no diagnosable error, because the throw sits outside the stage wrapper that logs.
          // Counting DERIVED_FROM as provenance keeps the check strict -- an unlinked derived fact
          // still fails it -- while making it correct for a node kind the check predates.
          // EXISTS rather than a second OPTIONAL MATCH: another MATCH would multiply rows per n
          // and inflate provenanceEdges / sourceMessages, which are counted from the same rows.
          RETURN count(DISTINCT n) AS learnedItems,
                 count(DISTINCT CASE
                   WHEN m IS NOT NULL OR EXISTS { (n)-[:DERIVED_FROM]->(:Fact) } THEN n END)
                   AS learnedItemsWithProvenance,
                 count(m) AS provenanceEdges,
                 count(DISTINCT m) AS sourceMessages
        }
        RETURN entities, facts, preferences, relationships,
               relationshipsWithProvenance, learnedItems, learnedItemsWithProvenance,
               provenanceEdges, sourceMessages, reasoningTraces, procedures
        """;

    private const string GoldCoverageQuery =
        """
        MATCH (n)-[:EXTRACTED_FROM]->(m:Message)
        WHERE n.owner_id = $ownerId
          AND (n:Entity OR n:Fact OR n:Preference)
          AND m.id IN $goldSourceMessageIds
        RETURN count(DISTINCT n) AS goldLearnedItems,
               count(DISTINCT m) AS goldSourceMessagesCovered
        """;

    public async Task<LongMemEvalGoldEvidenceCoverage?> ReadGoldCoverageAsync(
        string ownerId,
        IReadOnlyList<string> goldSourceMessageIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        ArgumentNullException.ThrowIfNull(goldSourceMessageIds);
        if (goldSourceMessageIds.Count == 0)
            return new LongMemEvalGoldEvidenceCoverage(0, 0, 0);


        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync<LongMemEvalGoldEvidenceCoverage?>(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                GoldCoverageQuery,
                new { ownerId, goldSourceMessageIds = goldSourceMessageIds.ToArray() })
                .ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new LongMemEvalGoldEvidenceCoverage(
                record["goldLearnedItems"].As<int>(),
                record["goldSourceMessagesCovered"].As<int>(),
                goldSourceMessageIds.Count);
        }).ConfigureAwait(false);
    }

    /// <summary>Every fact triple, entity name and preference this owner holds, as text.</summary>
    private const string MemoryTextQuery = """
        MATCH (f:Fact) WHERE f.owner_id = $ownerId AND f.invalidated_at IS NULL
        RETURN f.subject + ' ' + f.predicate + ' ' + f.object AS text
        UNION ALL
        MATCH (e:Entity) WHERE e.owner_id = $ownerId
        RETURN e.name + ' ' + coalesce(e.description, '') AS text
        UNION ALL
        MATCH (p:Preference) WHERE p.owner_id = $ownerId
        RETURN coalesce(p.category, '') + ' ' + coalesce(p.preference, '') AS text
        """;

    public async Task<IReadOnlyList<string>> ReadMemoryTextAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                MemoryTextQuery,
                new { ownerId }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return (IReadOnlyList<string>)records
                .Select(record => record["text"].As<string>())
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
        }).ConfigureAwait(false);
    }

    public async Task<LongMemEvalGraphSnapshot> ReadAsync(
        string ownerId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerId);
        await using var session = driver.AsyncSession();
        return await session.ExecuteReadAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(
                SnapshotQuery,
                new { ownerId }).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return new LongMemEvalGraphSnapshot(
                record["entities"].As<int>(),
                record["facts"].As<int>(),
                record["preferences"].As<int>(),
                record["relationships"].As<int>(),
                record["relationshipsWithProvenance"].As<int>(),
                record["learnedItems"].As<int>(),
                record["learnedItemsWithProvenance"].As<int>(),
                record["provenanceEdges"].As<int>(),
                record["sourceMessages"].As<int>(),
                record["reasoningTraces"].As<int>(),
                record["procedures"].As<int>());
        }).ConfigureAwait(false);
    }
}

/// <summary>
/// G3B.5. How much of the cold build traces back to the sessions that hold the answer.
/// </summary>
public sealed record LongMemEvalGoldEvidenceCoverage(
    int GoldLearnedItems,
    int GoldSourceMessagesCovered,
    int GoldSourceMessages)
{
    /// <summary>
    /// Zero means Structured mode <b>cannot</b> answer this question however good recall is: nothing
    /// the extractor learned came from a session containing the answer. That is an extraction
    /// finding, and must never be reported as a retrieval failure.
    /// </summary>
    public bool EvidenceLearned => GoldLearnedItems > 0;

    /// <summary>Fraction of answer-bearing source messages that contributed any learned item.</summary>
    public double SourceMessageCoverage =>
        GoldSourceMessages == 0 ? 0d : (double)GoldSourceMessagesCovered / GoldSourceMessages;
}

public sealed record LongMemEvalGraphSnapshot(
    int Entities,
    int Facts,
    int Preferences,
    int Relationships,
    int RelationshipsWithProvenance,
    int LearnedItems,
    int LearnedItemsWithProvenance,
    int ProvenanceEdges,
    int SourceMessages,
    int? ReasoningTraces = null,
    int? Procedures = null)
{
    /// <summary>
    /// Whether this snapshot matches one sealed in a manifest, comparing only what the SEALED side
    /// actually recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Record equality is the wrong comparison here, and it silently voided a whole corpus.</b>
    /// 6.5 added <see cref="ReasoningTraces"/> and <see cref="Procedures"/> as nullable precisely so a
    /// legacy manifest reads as <i>not measured</i> rather than as measured-and-zero. The per-question
    /// verification then compared with <c>Equals</c>, which compares every field — so a sealed
    /// snapshot with nulls could never equal a freshly probed one with counts, and every question in
    /// every pre-6.5 corpus failed as <c>prepared-graph-mismatch</c>. The graph was fine; the
    /// comparison was asking about a field the manifest was never able to record.
    /// </para>
    /// <para>
    /// So a null on the SEALED side means "not recorded, not compared". A null on the probed side is
    /// different and is not special-cased: that would mean the probe failed to count something it
    /// should have, which is a real mismatch.
    /// </para>
    /// </remarks>
    internal bool MatchesSealed(LongMemEvalGraphSnapshot sealedSnapshot)
    {
        ArgumentNullException.ThrowIfNull(sealedSnapshot);

        if (Entities != sealedSnapshot.Entities ||
            Facts != sealedSnapshot.Facts ||
            Preferences != sealedSnapshot.Preferences ||
            Relationships != sealedSnapshot.Relationships ||
            RelationshipsWithProvenance != sealedSnapshot.RelationshipsWithProvenance ||
            LearnedItems != sealedSnapshot.LearnedItems ||
            LearnedItemsWithProvenance != sealedSnapshot.LearnedItemsWithProvenance ||
            ProvenanceEdges != sealedSnapshot.ProvenanceEdges ||
            SourceMessages != sealedSnapshot.SourceMessages)
        {
            return false;
        }

        // Compared only when the seal recorded them. Added after several corpora were sealed, so an
        // absent value is a fact about the manifest's age, never about the graph.
        if (sealedSnapshot.ReasoningTraces is { } traces && ReasoningTraces != traces) return false;
        if (sealedSnapshot.Procedures is { } procedures && Procedures != procedures) return false;

        return true;
    }

    /// <summary>
    /// Entity + Fact + Preference + relationship count, <b>deliberately excluding traces</b>.
    /// </summary>
    /// <remarks>
    /// Traces are counted separately rather than folded in here. This number appears in every sealed
    /// measurement taken before the probe could see <c>:ReasoningTrace</c> at all, and quietly
    /// widening its definition would move recorded totals for a reason that has nothing to do with
    /// what was extracted — every prior build would appear to have grown.
    /// </remarks>
    public int TotalLearned => Entities + Facts + Preferences + Relationships;

    /// <summary>Everything the probe can see, traces included.</summary>
    public int TotalIncludingTraces => TotalLearned + (ReasoningTraces ?? 0);

    /// <summary>
    /// <see langword="true"/> when this snapshot actually looked for traces.
    /// </summary>
    /// <remarks>
    /// <b>Nullable rather than defaulted to 0, deliberately.</b> A manifest written before the probe
    /// could see <c>:ReasoningTrace</c> has no such field, and a non-nullable <c>int</c> would
    /// deserialize it to zero — reproducing, in the recorded data, the exact ambiguity 6.5 exists to
    /// remove: "measured, and there are none" would be indistinguishable from "never looked".
    /// </remarks>
    public bool TracesMeasured => ReasoningTraces.HasValue;

    /// <summary>
    /// <see langword="true"/> when the corpus holds no procedural/episodic memory at all.
    /// </summary>
    /// <remarks>
    /// The finding 6.5 exists to make visible. The probe was label-blind to <c>:ReasoningTrace</c>, so
    /// "the corpus contains no traces" and "the probe cannot see traces" produced identical output —
    /// and Phase 7's procedural work would have been measured against a graph nobody had confirmed
    /// contained anything to measure.
    /// </remarks>
    public bool HasNoTraces => ReasoningTraces == 0;

    public bool CompleteProvenance =>
        LearnedItemsWithProvenance == LearnedItems &&
        RelationshipsWithProvenance == Relationships;
}

/// <summary>What the store actually holds after a run, on the supersession axis.</summary>
/// <param name="SupersededByEdges">
/// The number that decides whether an arm measured anything. Zero means write-time supersession
/// never ran, and every score from that arm is an off-state score however the flag was set.
/// </param>
/// <param name="Facts">Total facts stored.</param>
/// <param name="InvalidatedFacts">Facts carrying <c>invalidated_at</c>.</param>
/// <param name="TopPredicates">
/// The most common predicates extraction produced. Present because a zero edge count is a symptom
/// and this is the diagnosis: supersession is refused for any predicate outside the six the relation
/// vocabulary declares single-valued, so the histogram shows immediately whether a corpus can
/// exercise the mechanism at all.
/// </param>
internal sealed record LongMemEvalSupersessionStore(
    int SupersededByEdges,
    int Facts,
    int InvalidatedFacts,
    IReadOnlyList<(string Predicate, int Count)> TopPredicates);

/// <summary>What kind of content reaches fact objects.</summary>
/// <param name="AmountBearing">
/// Objects carrying a value in the arithmetic corpus's own answer shape. <b>Presence, not
/// dominance, is the test</b>: one confirmed amount settles "stored at the wrong grain" against
/// "never captured", which is why no threshold was registered and none may be invented afterwards.
/// </param>
internal sealed record LongMemEvalFactObjectShape(
    int Facts,
    int AmountBearing,
    int OtherNumeric,
    int NonNumeric,
    IReadOnlyList<string> AmountSamples,
    IReadOnlyList<string> NumericSamples,
    IReadOnlyList<string> NonNumericSamples);

/// <summary>One subject-predicate pair holding more than one distinct object.</summary>
/// <param name="DistinctEntities">
/// How many <c>:Entity</c> nodes the facts reach through <c>ABOUT</c>. Several means the entities
/// were separated and only the subject STRING is under-specified; one means resolution merged
/// things that differ; zero means the triples are about nobody. Three defects, three fixes.
/// </param>
internal sealed record LongMemEvalAmbiguousSubject(
    string Subject, string PredicateKey, IReadOnlyList<string> Objects, int DistinctEntities);

/// <summary>Whether a retrieved value can be attributed to the thing it belongs to.</summary>
internal sealed record LongMemEvalSubjectAmbiguity(IReadOnlyList<LongMemEvalAmbiguousSubject> Pairs);
