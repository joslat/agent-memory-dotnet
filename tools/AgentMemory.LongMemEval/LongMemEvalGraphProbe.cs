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
    Task<LongMemEvalGoldEvidenceCoverage?> ReadGoldCoverageAsync(
        string ownerId,
        IReadOnlyList<string> goldSourceMessageIds,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<LongMemEvalGoldEvidenceCoverage?>(null);
}

internal sealed class Neo4jLongMemEvalGraphProbe(IDriver driver) : ILongMemEvalGraphProbe
{
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
               provenanceEdges, sourceMessages
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
                record["sourceMessages"].As<int>());
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
    int SourceMessages)
{
    public int TotalLearned => Entities + Facts + Preferences + Relationships;

    public bool CompleteProvenance =>
        LearnedItemsWithProvenance == LearnedItems &&
        RelationshipsWithProvenance == Relationships;
}
