using Neo4j.Driver;

namespace AgentMemory.LongMemEval;

internal interface ILongMemEvalGraphProbe
{
    Task<LongMemEvalGraphSnapshot> ReadAsync(
        string ownerId,
        CancellationToken cancellationToken = default);
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
