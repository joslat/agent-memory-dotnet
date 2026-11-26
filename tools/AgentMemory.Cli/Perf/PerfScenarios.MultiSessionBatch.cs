using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

public static partial class PerfScenarios
{
    private const int MultiSessionBatchUnitCount = 8;
    private const int MultiSessionBatchTokenBudget = 100_000;
    // Re-pinned 26 Nov 2026 (PERF-W11-ONE-OWNER), measured on the hermetic profile; last pinned 4 Aug 2026. Since then:
    // one owner per run (entity resolution reads each type's snapshot once per owner: 2 reads, was 16 with an owner per
    // source); the working-memory block is rebuilt after each source's commit (5 reads and 1 write each); each source's
    // commit runs one more entity and one more fact statement; every source's names are distinct texts (17 embedding
    // requests: the messages in one, then two per source; 40 items: 8 messages, 16 entities, 8 facts, 8 preferences).
    private const int MultiSessionEmbeddingRequestCount = 17;
    private const int MultiSessionEmbeddingItemCount = 40;
    private const int MultiSessionQueryCount = 227;
    private const int MultiSessionReadTransactionCount = 58;
    private const int MultiSessionWriteTransactionCount = 57;
    private const int MultiSessionCoalescedQueryCount = 99;
    private const int MultiSessionCoalescedReadTransactionCount = 42;
    private const int MultiSessionCoalescedWriteTransactionCount = 17;
    private const int MultiSessionPersistedItemCount = 8;

    private static async Task RunMultiSessionBatchAsync(ScenarioContext context, int batchSize)
    {
        var messages = Enumerable.Range(0, MultiSessionBatchUnitCount)
            .Select(unit => MultiSessionMessage(batchSize, context.Phase, context.Iteration, unit))
            .ToArray();

        await using var scope = context.Profile.Services.CreateAsyncScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var stored = await memory.AddMessagesAsync(messages, context.CancellationToken).ConfigureAwait(false);
        context.Turn.Add("store.messages", stored.Count);
        if (stored.Count != MultiSessionBatchUnitCount ||
            stored.Where((message, index) =>
                message.MessageId != messages[index].MessageId ||
                message.Embedding is not { Length: > 0 } embedding ||
                embedding.Length != context.Profile.Dimensions).Any())
        {
            throw new InvalidOperationException(
                $"PERF-W-11-B{batchSize:D2} did not store all eight exact embedded source messages.");
        }

        // Deliberately reverse the requests. The product batch pipeline must restore source chronology
        // before model batching and before the sequential resolution/persistence commits.
        var requests = messages.AsEnumerable().Reverse().Select(message => new ExtractionRequest
        {
            Messages = [message],
            SessionId = message.SessionId,
            // PERF-W11-ONE-OWNER: one owner for the run. Batches are formed per owner (RVW-BATCH-OWNERS), so an owner
            // per source would measure one-session batches whatever the batch size.
            UserId = MultiSessionOwnerId(batchSize, context.Phase, context.Iteration),
            TypesToExtract = ExtractionTypes.All,
        }).ToArray();

        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
        var results = await pipeline.ExtractBatchAsync(
            requests,
            batchSize,
            MultiSessionBatchTokenBudget,
            context.CancellationToken).ConfigureAwait(false);

        var expectedCalls = MultiSessionBatchUnitCount / batchSize;
        var chronologicalSessions = messages.Select(message => message.SessionId).ToArray();
        var returnedSessions = results
            .Select(result => result.Metadata.TryGetValue("sessionId", out var value) ? value as string : null)
            .ToArray();
        var outputsExact = results.Count == MultiSessionBatchUnitCount && results.All(result =>
            result.Status == IngestionStatus.Succeeded &&
            result.Entities.Count == 2 &&
            result.Facts.Count == 1 &&
            result.Preferences.Count == 1 &&
            result.Relationships.Count == 1 &&
            result.SourceMessageIds.Count == 1);
        var orderExact = returnedSessions.SequenceEqual(chronologicalSessions, StringComparer.Ordinal);
        var expectedReads = context.Profile.UseCoalescedPersistenceTransactions
            ? MultiSessionCoalescedReadTransactionCount : MultiSessionReadTransactionCount;
        var expectedWrites = context.Profile.UseCoalescedPersistenceTransactions
            ? MultiSessionCoalescedWriteTransactionCount : MultiSessionWriteTransactionCount;
        var expectedQueries = context.Profile.UseCoalescedPersistenceTransactions
            ? MultiSessionCoalescedQueryCount : MultiSessionQueryCount;
        var callsExact =
            context.Turn.Counter("llm.calls") == expectedCalls &&
            context.Turn.Counter("llm.unified_batch.calls") == expectedCalls &&
            context.Turn.Counter("llm.unified.calls") == 0;
        var fixedWorkExact =
            context.Turn.Counter("embed.requests") == MultiSessionEmbeddingRequestCount &&
            context.Turn.Counter("embed.items") == MultiSessionEmbeddingItemCount &&
            context.Turn.SpanCounts.GetValueOrDefault("provider.embedding") == MultiSessionEmbeddingRequestCount &&
            context.Turn.Counter("neo4j.queries") == expectedQueries &&
            context.Turn.Counter("neo4j.tx.read") == expectedReads &&
            context.Turn.Counter("neo4j.tx.write") == expectedWrites &&
            context.Turn.Counter("persist.entities") == MultiSessionBatchUnitCount * 2 &&
            context.Turn.Counter("persist.facts") == MultiSessionPersistedItemCount &&
            context.Turn.Counter("persist.preferences") == MultiSessionPersistedItemCount &&
            context.Turn.Counter("persist.relationships") == MultiSessionPersistedItemCount &&
            context.Turn.Counter("store.messages") == MultiSessionBatchUnitCount &&
            context.Turn.SpanCounts.GetValueOrDefault("memory.persist.total") == MultiSessionBatchUnitCount;

        context.Turn.Add("batch.source_sessions", MultiSessionBatchUnitCount);
        context.Turn.Add("batch.max_sessions", batchSize);
        context.Turn.Add("batch.expected_calls", expectedCalls);
        context.Turn.Add("batch.output_exact", outputsExact ? 1 : 0);
        context.Turn.Add("batch.commit_order_exact", orderExact ? 1 : 0);

        if (!outputsExact || !orderExact || !callsExact || !fixedWorkExact)
        {
            throw new InvalidOperationException(
                $"PERF-W-11-B{batchSize:D2} batch contract failed (outputs/order=" +
                $"{outputsExact}/{orderExact}; llm/batch/single=" +
                $"{context.Turn.Counter("llm.calls")}/" +
                $"{context.Turn.Counter("llm.unified_batch.calls")}/" +
                $"{context.Turn.Counter("llm.unified.calls")}, expected {expectedCalls}/{expectedCalls}/0; " +
                $"embed.requests/items/provider={context.Turn.Counter("embed.requests")}/" +
                $"{context.Turn.Counter("embed.items")}/{context.Turn.SpanCounts.GetValueOrDefault("provider.embedding")}, expected " +
                $"{MultiSessionEmbeddingRequestCount}/{MultiSessionEmbeddingItemCount}/{MultiSessionEmbeddingRequestCount}; " +
                $"queries/read/write={context.Turn.Counter("neo4j.queries")}/" +
                $"{context.Turn.Counter("neo4j.tx.read")}/{context.Turn.Counter("neo4j.tx.write")}, expected " +
                $"{expectedQueries}/{expectedReads}/{expectedWrites}; " +
                $"fixed_work_exact={fixedWorkExact}).");
        }
    }

    private static async Task VerifyMultiSessionBatchAsync(
        ScenarioVerificationContext context,
        int batchSize)
    {
        // PERF-W11-ONE-OWNER: every source shares the run's owner, so each unit is read back through its own source
        // message (what was extracted from it), then the owner as a whole (nothing folded across units, nothing crossed
        // owners), and only then is anything cleaned.
        var ownerId = MultiSessionOwnerId(batchSize, context.Phase, context.Iteration);
        var sessionIds = Enumerable.Range(0, MultiSessionBatchUnitCount)
            .Select(unit => MultiSessionSessionId(batchSize, context.Phase, context.Iteration, unit)).ToArray();
        await using var session = context.Profile.Driver.AsyncSession();
        for (var unit = 0; unit < MultiSessionBatchUnitCount; unit++)
        {
            var sessionId = sessionIds[unit];
            var messageId = $"{sessionId}-message";
            const string verifyCypher = """
                CALL {
                    MATCH (m:Message {session_id: $sessionId})
                    RETURN count(m) AS messages,
                           count(CASE WHEN size(m.embedding) = $dimensions THEN 1 END) AS messageVectors
                }
                CALL {
                    MATCH (e:Entity {owner_id: $ownerId})-[:EXTRACTED_FROM]->(:Message {id: $messageId})
                    RETURN count(DISTINCT e) AS entities
                }
                CALL {
                    MATCH (f:Fact {owner_id: $ownerId})-[:EXTRACTED_FROM]->(:Message {id: $messageId})
                    RETURN count(DISTINCT f) AS facts
                }
                CALL {
                    MATCH (p:Preference {owner_id: $ownerId})-[:EXTRACTED_FROM]->(:Message {id: $messageId})
                    RETURN count(DISTINCT p) AS preferences
                }
                CALL {
                    MATCH (:Entity {owner_id: $ownerId})-[r:RELATED_TO]->(:Entity {owner_id: $ownerId})
                    WHERE r.owner_id = $ownerId AND r.relation_type = 'WORKS_AT' AND $messageId IN r.source_message_ids
                    RETURN count(r) AS relationships
                }
                CALL {
                    MATCH (memory)-[:EXTRACTED_FROM]->(:Message {id: $messageId})
                    WHERE memory.owner_id = $ownerId
                    RETURN count(*) AS provenance
                }
                RETURN messages, messageVectors, entities, facts, preferences, relationships, provenance
                """;
            var cursor = await session.RunAsync(
                verifyCypher,
                new { sessionId, ownerId, messageId, dimensions = context.Profile.Dimensions })
                .ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            var exact =
                record["messages"].As<long>() == 1 &&
                record["messageVectors"].As<long>() == 1 &&
                record["entities"].As<long>() == 2 &&
                record["facts"].As<long>() == 1 &&
                record["preferences"].As<long>() == 1 &&
                record["relationships"].As<long>() == 1 &&
                record["provenance"].As<long>() == 4;
            if (!exact)
                throw new InvalidOperationException(
                    $"PERF-W-11-B{batchSize:D2} graph/provenance failed for source session {unit} (messages/vectors/entities/" +
                    $"facts/preferences/relationships/provenance={record["messages"]}/{record["messageVectors"]}/" +
                    $"{record["entities"]}/{record["facts"]}/{record["preferences"]}/{record["relationships"]}/{record["provenance"]}).");
        }

        const string ownerCypher = """
            CALL { MATCH (e:Entity {owner_id: $ownerId}) RETURN count(e) AS entities }
            CALL { MATCH (f:Fact {owner_id: $ownerId}) RETURN count(f) AS facts }
            CALL { MATCH (p:Preference {owner_id: $ownerId}) RETURN count(p) AS preferences }
            CALL {
                MATCH (:Entity)-[r:RELATED_TO]->(:Entity)
                WHERE r.owner_id = $ownerId
                RETURN count(r) AS relationships
            }
            CALL {
                MATCH (source:Entity)-[r:RELATED_TO]->(target:Entity)
                WHERE r.owner_id = $ownerId
                  AND (source.owner_id <> $ownerId OR target.owner_id <> $ownerId)
                RETURN count(r) AS crossOwnerEdges
            }
            RETURN entities, facts, preferences, relationships, crossOwnerEdges
            """;
        var owner = await (await session.RunAsync(ownerCypher, new { ownerId }).ConfigureAwait(false))
            .SingleAsync().ConfigureAwait(false);
        if (owner["entities"].As<long>() != MultiSessionBatchUnitCount * 2 ||
            owner["facts"].As<long>() != MultiSessionBatchUnitCount ||
            owner["preferences"].As<long>() != MultiSessionBatchUnitCount ||
            owner["relationships"].As<long>() != MultiSessionBatchUnitCount ||
            owner["crossOwnerEdges"].As<long>() != 0)
            throw new InvalidOperationException(
                $"PERF-W-11-B{batchSize:D2} owner shape/isolation failed (entities/facts/preferences/relationships/cross=" +
                $"{owner["entities"]}/{owner["facts"]}/{owner["preferences"]}/{owner["relationships"]}/{owner["crossOwnerEdges"]}).");

        const string cleanupCypher = """
            MATCH (n)
            WHERE n.owner_id = $ownerId
               OR n.session_id IN $sessionIds
               OR n.id IN $conversationIds
            DETACH DELETE n
            WITH count(n) AS deleted
            OPTIONAL MATCH (remaining)
            WHERE remaining.owner_id = $ownerId
               OR remaining.session_id IN $sessionIds
               OR remaining.id IN $conversationIds
            RETURN deleted, count(remaining) AS remaining
            """;
        var cleanup = await session.RunAsync(
            cleanupCypher,
            new { ownerId, sessionIds, conversationIds = sessionIds.Select(id => $"{id}-conversation").ToArray() })
            .ConfigureAwait(false);
        var cleanupRecord = await cleanup.SingleAsync().ConfigureAwait(false);
        if (cleanupRecord["deleted"].As<long>() == 0 || cleanupRecord["remaining"].As<long>() != 0)
            throw new InvalidOperationException($"PERF-W-11-B{batchSize:D2} did not clean its owner and sources.");
    }

    private static Message MultiSessionMessage(int batchSize, string phase, int iteration, int unit)
    {
        var sessionId = MultiSessionSessionId(batchSize, phase, iteration, unit);
        return new Message
        {
            MessageId = $"{sessionId}-message",
            ConversationId = $"{sessionId}-conversation",
            SessionId = sessionId,
            Role = "user",
            Content = $"LAB-B1 source {unit:D2}: Person {unit:D2} works at Company {unit:D2} and prefers tea.",
            TimestampUtc = new DateTimeOffset(2026, 2, 1, 12, 0, 0, TimeSpan.Zero).AddMinutes(unit),
        };
    }

    private static string MultiSessionSessionId(int batchSize, string phase, int iteration, int unit) =>
        $"perf-w11-b{batchSize:D2}-{phase}-{iteration}-session-{unit:D2}";

    private static string MultiSessionOwnerId(int batchSize, string phase, int iteration) =>
        $"perf-w11-b{batchSize:D2}-{phase}-{iteration}-owner";
}
