using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

public static partial class PerfScenarios
{
    private const int MultiSessionBatchUnitCount = 8;
    private const int MultiSessionBatchTokenBudget = 100_000;

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
        var requests = messages.AsEnumerable().Reverse().Select((message, reverseIndex) =>
        {
            var unit = MultiSessionBatchUnitCount - reverseIndex - 1;
            return new ExtractionRequest
            {
                Messages = [message],
                SessionId = message.SessionId,
                UserId = MultiSessionOwnerId(batchSize, context.Phase, context.Iteration, unit),
                TypesToExtract = ExtractionTypes.All,
            };
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
        var callsExact =
            context.Turn.Counter("llm.calls") == expectedCalls &&
            context.Turn.Counter("llm.unified_batch.calls") == expectedCalls &&
            context.Turn.Counter("llm.unified.calls") == 0;

        context.Turn.Add("batch.source_sessions", MultiSessionBatchUnitCount);
        context.Turn.Add("batch.max_sessions", batchSize);
        context.Turn.Add("batch.expected_calls", expectedCalls);
        context.Turn.Add("batch.output_exact", outputsExact ? 1 : 0);
        context.Turn.Add("batch.commit_order_exact", orderExact ? 1 : 0);

        if (!outputsExact || !orderExact || !callsExact)
        {
            throw new InvalidOperationException(
                $"PERF-W-11-B{batchSize:D2} batch contract failed (outputs/order=" +
                $"{outputsExact}/{orderExact}; llm/batch/single=" +
                $"{context.Turn.Counter("llm.calls")}/" +
                $"{context.Turn.Counter("llm.unified_batch.calls")}/" +
                $"{context.Turn.Counter("llm.unified.calls")}, expected {expectedCalls}/{expectedCalls}/0)."
            );
        }
    }

    private static async Task VerifyMultiSessionBatchAsync(
        ScenarioVerificationContext context,
        int batchSize)
    {
        for (var unit = 0; unit < MultiSessionBatchUnitCount; unit++)
        {
            var sessionId = MultiSessionSessionId(batchSize, context.Phase, context.Iteration, unit);
            var ownerId = MultiSessionOwnerId(batchSize, context.Phase, context.Iteration, unit);
            var messageId = $"{sessionId}-message";
            const string verifyCypher = """
                CALL {
                    MATCH (m:Message {session_id: $sessionId})
                    RETURN count(m) AS messages,
                           count(CASE WHEN size(m.embedding) = $dimensions THEN 1 END) AS messageVectors
                }
                CALL { MATCH (e:Entity {owner_id: $ownerId}) RETURN count(e) AS entities }
                CALL { MATCH (f:Fact {owner_id: $ownerId}) RETURN count(f) AS facts }
                CALL { MATCH (p:Preference {owner_id: $ownerId}) RETURN count(p) AS preferences }
                CALL {
                    MATCH (:Entity {owner_id: $ownerId})-[r:RELATED_TO]->(:Entity {owner_id: $ownerId})
                    WHERE r.owner_id = $ownerId AND r.relation_type = 'WORKS_AT'
                    RETURN count(r) AS relationships,
                           count(CASE WHEN $messageId IN r.source_message_ids THEN 1 END) AS relationshipSources
                }
                CALL {
                    MATCH (memory)-[:EXTRACTED_FROM]->(:Message {id: $messageId})
                    WHERE memory.owner_id = $ownerId
                    RETURN count(*) AS provenance
                }
                CALL {
                    MATCH (source:Entity)-[r:RELATED_TO]->(target:Entity)
                    WHERE r.owner_id = $ownerId
                      AND (source.owner_id <> $ownerId OR target.owner_id <> $ownerId)
                    RETURN count(r) AS crossOwnerEdges
                }
                RETURN messages, messageVectors, entities, facts, preferences, relationships,
                       relationshipSources, provenance, crossOwnerEdges
                """;

            await using var session = context.Profile.Driver.AsyncSession();
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
                record["relationshipSources"].As<long>() == 1 &&
                record["provenance"].As<long>() == 4 &&
                record["crossOwnerEdges"].As<long>() == 0;
            if (!exact)
                throw new InvalidOperationException(
                    $"PERF-W-11-B{batchSize:D2} graph/provenance/isolation failed for source session {unit}.");

            const string cleanupCypher = """
                MATCH (n)
                WHERE n.owner_id = $ownerId
                   OR n.session_id = $sessionId
                   OR n.id = $conversationId
                DETACH DELETE n
                WITH count(n) AS deleted
                OPTIONAL MATCH (remaining)
                WHERE remaining.owner_id = $ownerId
                   OR remaining.session_id = $sessionId
                   OR remaining.id = $conversationId
                RETURN deleted, count(remaining) AS remaining
                """;
            var cleanup = await session.RunAsync(
                cleanupCypher,
                new { sessionId, ownerId, conversationId = $"{sessionId}-conversation" })
                .ConfigureAwait(false);
            var cleanupRecord = await cleanup.SingleAsync().ConfigureAwait(false);
            if (cleanupRecord["deleted"].As<long>() == 0 || cleanupRecord["remaining"].As<long>() != 0)
                throw new InvalidOperationException(
                    $"PERF-W-11-B{batchSize:D2} did not clean source session {unit}.");
        }
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

    private static string MultiSessionOwnerId(int batchSize, string phase, int iteration, int unit) =>
        $"perf-w11-b{batchSize:D2}-{phase}-{iteration}-owner-{unit:D2}";
}
