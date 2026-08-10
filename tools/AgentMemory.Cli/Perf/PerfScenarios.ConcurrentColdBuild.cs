using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

public static partial class PerfScenarios
{
    private const int ColdBuildUnitCount = 10;
    private const int ColdBuildEntitiesPerUnit = 2;
    private const int ColdBuildFactsPerUnit = 2;
    private const int ColdBuildPreferencesPerUnit = 1;
    private const int ColdBuildRelationshipsPerUnit = 1;
    private const int ColdBuildEmbeddingsPerUnit = 8;

    /// <summary>
    /// Embedding <b>requests</b> per unit, which is no longer the same as items.
    /// </summary>
    /// <remarks>
    /// This contract used to assert <c>embed.requests == embed.items</c> — one request per item,
    /// which is exactly "no batching". Default-on learned-memory embedding batching deliberately
    /// broke that, and because PERF-W-10-C* is <c>IncludeInDefaultRun: false</c> the scenario simply
    /// stopped passing and nobody was told: it reported 40 requests against an expected 80 and failed
    /// the contract <b>because the product had improved</b>.
    /// <para>
    /// The invariant worth keeping is that <b>every item is still embedded</b> — <c>embed.items</c>
    /// is unchanged at 8 per unit. Requests are pinned exactly rather than bounded, so a change in
    /// batch size fails this contract and has to be re-approved, the same discipline the Cypher
    /// snapshot uses.
    /// </para>
    /// </remarks>
    private const int ColdBuildEmbeddingRequestsPerUnit = 4;
    private const int ColdBuildReadsPerUnit = 4;
    private const int ColdBuildWritesPerUnit = 7;
    private const int ColdBuildQueriesPerUnit = 27;

    private static async Task RunConcurrentColdBuildAsync(ScenarioContext context, int workers)
    {
        var work = Enumerable.Range(0, ColdBuildUnitCount)
            .Select(unit => (Func<CancellationToken, Task<ColdBuildUnitResult>>)(token =>
                RunColdBuildUnitAsync(context, workers, unit, token)))
            .ToArray();

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var processorTimeBefore = process.TotalProcessorTime;
        var waveStartedAt = Stopwatch.GetTimestamp();
        var result = await BoundedWorkScheduler
            .RunAsync(work, workers, context.CancellationToken)
            .ConfigureAwait(false);
        var waveDuration = Stopwatch.GetElapsedTime(waveStartedAt).TotalMilliseconds;

        process.Refresh();
        var processorTimeMs = (process.TotalProcessorTime - processorTimeBefore).TotalMilliseconds;

        context.Turn.Add("cold_build.units", result.Results.Count);
        context.Turn.Add("cold_build.workers", workers);
        context.Turn.Add("cold_build.max_concurrency", result.MaxConcurrency);
        context.Turn.RecordSample("cold_build.wave_ms", waveDuration);
        context.Turn.RecordSample("cold_build.process_cpu_ms", processorTimeMs);
        foreach (var unit in result.Results)
            context.Turn.RecordSample("cold_build.unit_ms", unit.DurationMs);

        var calls = context.Turn.Counter("llm.unified.calls");
        context.Turn.Add("llm.unified.retries", Math.Max(0, calls - ColdBuildUnitCount));
        var expectedReadsPerUnit = context.Profile.UseCoalescedPersistenceTransactions
            ? 2 : ColdBuildReadsPerUnit;
        var expectedWritesPerUnit = context.Profile.UseCoalescedPersistenceTransactions
            ? 2 : ColdBuildWritesPerUnit;
        var expectedQueriesPerUnit = context.Profile.UseCoalescedPersistenceTransactions
            // 11 -> 9: canonical fact identity and the fused write path removed two queries per
            // unit without changing the transaction count (reads/writes stay 2/2), which is the same
            // family of movement ledger seq 5 records for PERF-W-02.
            ? 9 : ColdBuildQueriesPerUnit;

        var outputsExact = result.Results.All(unit =>
            unit.Status == IngestionStatus.Succeeded &&
            unit.EntityCount == ColdBuildEntitiesPerUnit &&
            unit.FactCount == ColdBuildFactsPerUnit &&
            unit.PreferenceCount == ColdBuildPreferencesPerUnit &&
            unit.RelationshipCount == ColdBuildRelationshipsPerUnit &&
            unit.SourceMessageCount == 1);
        var countersExact =
            context.Turn.Counter("llm.calls") == ColdBuildUnitCount &&
            calls == ColdBuildUnitCount &&
            context.Turn.Counter("llm.unified.retries") == 0 &&
            context.Turn.Counter("store.messages") == ColdBuildUnitCount &&
            context.Turn.Counter("persist.entities") ==
                ColdBuildUnitCount * ColdBuildEntitiesPerUnit &&
            context.Turn.Counter("persist.facts") ==
                ColdBuildUnitCount * ColdBuildFactsPerUnit &&
            context.Turn.Counter("persist.preferences") ==
                ColdBuildUnitCount * ColdBuildPreferencesPerUnit &&
            context.Turn.Counter("persist.relationships") ==
                ColdBuildUnitCount * ColdBuildRelationshipsPerUnit &&
            context.Turn.Counter("embed.requests") ==
                ColdBuildUnitCount * ColdBuildEmbeddingRequestsPerUnit &&
            context.Turn.Counter("embed.items") ==
                ColdBuildUnitCount * ColdBuildEmbeddingsPerUnit &&
            context.Turn.Counter("neo4j.tx.read") ==
                ColdBuildUnitCount * expectedReadsPerUnit &&
            context.Turn.Counter("neo4j.tx.write") ==
                ColdBuildUnitCount * expectedWritesPerUnit &&
            context.Turn.Counter("neo4j.queries") ==
                ColdBuildUnitCount * expectedQueriesPerUnit;

        if (!outputsExact ||
            !countersExact ||
            result.MaxConcurrency != workers ||
            context.Profile.MaxConnectionPoolSize != 16)
        {
            throw new InvalidOperationException(
                $"PERF-W-10-C{workers:D2} cold-build contract failed (outputs_exact={outputsExact}, " +
                $"max_concurrency={result.MaxConcurrency}/{workers}, pool=" +
                $"{context.Profile.MaxConnectionPoolSize}/16, llm/unified/retries=" +
                $"{context.Turn.Counter("llm.calls")}/{calls}/" +
                $"{context.Turn.Counter("llm.unified.retries")}, expected 10/10/0; " +
                $"stored={context.Turn.Counter("store.messages")}/10; persisted=" +
                $"{context.Turn.Counter("persist.entities")}/" +
                $"{context.Turn.Counter("persist.facts")}/" +
                $"{context.Turn.Counter("persist.preferences")}/" +
                $"{context.Turn.Counter("persist.relationships")}, expected 20/20/10/10; " +
                $"embed requests/items={context.Turn.Counter("embed.requests")}/" +
                $"{context.Turn.Counter("embed.items")}, expected " +
                $"{ColdBuildUnitCount * ColdBuildEmbeddingRequestsPerUnit}/" +
                $"{ColdBuildUnitCount * ColdBuildEmbeddingsPerUnit}; reads/writes/queries=" +
                $"{context.Turn.Counter("neo4j.tx.read")}/" +
                $"{context.Turn.Counter("neo4j.tx.write")}/" +
                $"{context.Turn.Counter("neo4j.queries")}, expected " +
                $"{10 * expectedReadsPerUnit}/{10 * expectedWritesPerUnit}/" +
                $"{10 * expectedQueriesPerUnit}).");
        }
    }

    private static async Task<ColdBuildUnitResult> RunColdBuildUnitAsync(
        ScenarioContext context,
        int workers,
        int unit,
        CancellationToken cancellationToken)
    {
        var sessionId = ColdBuildSessionId(workers, context.Phase, context.Iteration, unit);
        var ownerId = ColdBuildOwnerId(workers, context.Phase, context.Iteration, unit);
        var message = new Message
        {
            MessageId = $"{sessionId}-msg-00",
            ConversationId = $"{sessionId}-conversation",
            SessionId = sessionId,
            Role = "user",
            Content = $"{UnifiedExtractionProbeMessage} Unit {unit:D2}.",
            TimestampUtc = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero)
                .AddMinutes(unit),
        };

        await using var scope = context.Profile.Services.CreateAsyncScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var startedAt = Stopwatch.GetTimestamp();
        var stored = await memory.AddMessagesAsync([message], cancellationToken).ConfigureAwait(false);
        context.Turn.Add("store.messages", stored.Count);
        if (stored.Count != 1 ||
            stored[0].MessageId != message.MessageId ||
            stored[0].Embedding is not { Length: > 0 } embedding ||
            embedding.Length != context.Profile.Dimensions)
        {
            throw new InvalidOperationException(
                $"Cold-build unit {unit} did not store its exact embedded source message.");
        }

        var extracted = await memory.ExtractAndPersistAsync(
            new ExtractionRequest
            {
                Messages = [message],
                SessionId = sessionId,
                UserId = ownerId,
                TypesToExtract = ExtractionTypes.All,
            },
            cancellationToken).ConfigureAwait(false);

        return new ColdBuildUnitResult(
            extracted.Status,
            extracted.Entities.Count,
            extracted.Facts.Count,
            extracted.Preferences.Count,
            extracted.Relationships.Count,
            extracted.SourceMessageIds.Count,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
    }

    private static async Task VerifyConcurrentColdBuildAsync(
        ScenarioVerificationContext context,
        int workers)
    {
        for (var unit = 0; unit < ColdBuildUnitCount; unit++)
        {
            var sessionId = ColdBuildSessionId(workers, context.Phase, context.Iteration, unit);
            var ownerId = ColdBuildOwnerId(workers, context.Phase, context.Iteration, unit);
            var messageId = $"{sessionId}-msg-00";
            const string cypher = """
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
                           count(CASE WHEN $messageId IN r.source_message_ids THEN 1 END)
                               AS relationshipSources
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
                cypher,
                new
                {
                    sessionId,
                    ownerId,
                    messageId,
                    dimensions = context.Profile.Dimensions,
                }).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);

            var graphExact =
                record["messages"].As<long>() == 1 &&
                record["messageVectors"].As<long>() == 1 &&
                record["entities"].As<long>() == ColdBuildEntitiesPerUnit &&
                record["facts"].As<long>() == ColdBuildFactsPerUnit &&
                record["preferences"].As<long>() == ColdBuildPreferencesPerUnit &&
                record["relationships"].As<long>() == ColdBuildRelationshipsPerUnit &&
                record["relationshipSources"].As<long>() == ColdBuildRelationshipsPerUnit &&
                record["provenance"].As<long>() ==
                    ColdBuildEntitiesPerUnit + ColdBuildFactsPerUnit + ColdBuildPreferencesPerUnit &&
                record["crossOwnerEdges"].As<long>() == 0;
            if (!graphExact)
            {
                throw new InvalidOperationException(
                    $"PERF-W-10-C{workers:D2} graph verification failed for unit {unit}; exact " +
                    "message/vector, 2/2/1/1 learned shape, provenance, and owner isolation are required.");
            }

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
                new
                {
                    sessionId,
                    ownerId,
                    conversationId = $"{sessionId}-conversation",
                }).ConfigureAwait(false);
            var cleanupRecord = await cleanup.SingleAsync().ConfigureAwait(false);
            if (cleanupRecord["deleted"].As<long>() == 0 || cleanupRecord["remaining"].As<long>() != 0)
                throw new InvalidOperationException($"PERF-W-10-C{workers:D2} did not clean unit {unit}.");
        }
    }

    private static string ColdBuildSessionId(
        int workers,
        string phase,
        int iteration,
        int unit) =>
        $"perf-w10-c{workers:D2}-{phase}-{iteration}-session-{unit:D2}";

    private static string ColdBuildOwnerId(
        int workers,
        string phase,
        int iteration,
        int unit) =>
        $"perf-w10-c{workers:D2}-{phase}-{iteration}-owner-{unit:D2}";

    private sealed record ColdBuildUnitResult(
        IngestionStatus Status,
        int EntityCount,
        int FactCount,
        int PreferenceCount,
        int RelationshipCount,
        int SourceMessageCount,
        double DurationMs);
}
