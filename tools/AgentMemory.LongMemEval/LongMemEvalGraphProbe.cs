using Neo4j.Driver;

namespace AgentMemory.LongMemEval;

internal interface ILongMemEvalGraphProbe
{
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
          RETURN count(DISTINCT n) AS learnedItems,
                 count(DISTINCT CASE WHEN m IS NOT NULL THEN n END) AS learnedItemsWithProvenance,
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
