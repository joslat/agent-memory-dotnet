using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

public static partial class PerfScenarios
{
    private const int IntegratedOwnerCount = 10;
    private const int IntegratedSessionsPerOwner = 4;
    private const int IntegratedMessagesPerSession = 12;
    private const int IntegratedSourceSessionCount =
        IntegratedOwnerCount * IntegratedSessionsPerOwner;
    private const int IntegratedMessageCount =
        IntegratedSourceSessionCount * IntegratedMessagesPerSession;
    private const int IntegratedBatchTokenBudget = 100_000;
    private const int IntegratedEmbeddingRequestCount = 130;
    private const int IntegratedEmbeddingItemCount = 720;
    private const int IntegratedLegacyQueryCount = 930;
    private const int IntegratedSnapshotQueryCount = 870;
    private const int IntegratedCoalescedLegacyQueryCount = 290;
    private const int IntegratedCoalescedSnapshotQueryCount = 230;
    private const int IntegratedLegacyReadTransactionCount = 120;
    private const int IntegratedSnapshotReadTransactionCount = 60;
    private const int IntegratedCoalescedLegacyReadTransactionCount = 80;
    private const int IntegratedCoalescedSnapshotReadTransactionCount = 20;
    private const int IntegratedLegacyWriteTransactionCount = 250;
    private const int IntegratedCoalescedWriteTransactionCount = 50;

    private static async Task RunIntegratedColdBuildAsync(ScenarioContext context, int workers)
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var processorTimeBefore = process.TotalProcessorTime;
        var totalStartedAt = Stopwatch.GetTimestamp();

        var rawWork = Enumerable.Range(0, IntegratedOwnerCount)
            .Select(owner => (Func<CancellationToken, Task<IntegratedOwnerInput>>)(token =>
                StoreIntegratedOwnerAsync(context, workers, owner, token)))
            .ToArray();
        var rawStartedAt = Stopwatch.GetTimestamp();
        var raw = await BoundedWorkScheduler
            .RunAsync(rawWork, workers, context.CancellationToken)
            .ConfigureAwait(false);
        var rawWaveMs = Stopwatch.GetElapsedTime(rawStartedAt).TotalMilliseconds;
        context.Turn.Add("store.messages", raw.Results.Sum(result => result.Messages.Count));

        var extractionWork = raw.Results
            .Select(input => (Func<CancellationToken, Task<IntegratedOwnerResult>>)(token =>
                ExtractIntegratedOwnerAsync(context, input, token)))
            .ToArray();
        var extractionStartedAt = Stopwatch.GetTimestamp();
        var extracted = await BoundedWorkScheduler
            .RunAsync(extractionWork, workers, context.CancellationToken)
            .ConfigureAwait(false);
        var extractionWaveMs = Stopwatch.GetElapsedTime(extractionStartedAt).TotalMilliseconds;
        var totalWaveMs = Stopwatch.GetElapsedTime(totalStartedAt).TotalMilliseconds;

        process.Refresh();
        var processorTimeMs = (process.TotalProcessorTime - processorTimeBefore).TotalMilliseconds;

        context.Turn.Add("integrated.owners", IntegratedOwnerCount);
        context.Turn.Add("integrated.source_sessions", IntegratedSourceSessionCount);
        context.Turn.Add("integrated.messages", IntegratedMessageCount);
        context.Turn.Add("integrated.workers", workers);
        context.Turn.Add("integrated.raw.max_concurrency", raw.MaxConcurrency);
        context.Turn.Add("integrated.extract.max_concurrency", extracted.MaxConcurrency);
        context.Turn.Add(
            "integrated.plan_batches",
            extracted.Results.Sum(result => result.Plan.BatchCount));
        context.Turn.Add(
            "integrated.plan_sessions",
            extracted.Results.Sum(result => result.Plan.SourceSessionCount));
        context.Turn.Add(
            "integrated.plan_tokens_est",
            extracted.Results.Sum(result => result.Plan.TotalEstimatedInputTokens));
        context.Turn.RecordSample("integrated.raw_wave_ms", rawWaveMs);
        context.Turn.RecordSample("integrated.extract_wave_ms", extractionWaveMs);
        context.Turn.RecordSample("integrated.total_wave_ms", totalWaveMs);
        context.Turn.RecordSample("integrated.process_cpu_ms", processorTimeMs);
        foreach (var owner in raw.Results)
            context.Turn.RecordSample("integrated.owner_raw_ms", owner.DurationMs);
        foreach (var owner in extracted.Results)
            context.Turn.RecordSample("integrated.owner_extract_ms", owner.DurationMs);

        var expectedSessions = raw.Results
            .SelectMany(input => input.ChronologicalRequests)
            .Select(request => request.SessionId)
            .ToArray();
        var returnedSessions = extracted.Results
            .SelectMany(result => result.Results)
            .Select(result => result.Metadata.TryGetValue("sessionId", out var value) ? value as string : null)
            .ToArray();
        var outputsExact = extracted.Results
            .SelectMany(result => result.Results)
            .All(result =>
                result.Status == IngestionStatus.Succeeded &&
                result.Entities.Count == 2 &&
                result.Facts.Count == 1 &&
                result.Preferences.Count == 1 &&
                result.Relationships.Count == 1 &&
                result.SourceMessageIds.Count == IntegratedMessagesPerSession);
        var orderExact = returnedSessions.SequenceEqual(expectedSessions, StringComparer.Ordinal);
        var calls = context.Turn.Counter("llm.unified_batch.calls");
        var retries = Math.Max(0, calls - IntegratedOwnerCount);
        context.Turn.Add("llm.unified_batch.retries", retries);

        var expectedQueries = context.Profile.UseCoalescedPersistenceTransactions
            ? context.Profile.UseBatchEntityResolutionSnapshots
                ? IntegratedCoalescedSnapshotQueryCount
                : IntegratedCoalescedLegacyQueryCount
            : context.Profile.UseBatchEntityResolutionSnapshots
                ? IntegratedSnapshotQueryCount
                : IntegratedLegacyQueryCount;
        var expectedReads = context.Profile.UseCoalescedPersistenceTransactions
            ? context.Profile.UseBatchEntityResolutionSnapshots
                ? IntegratedCoalescedSnapshotReadTransactionCount
                : IntegratedCoalescedLegacyReadTransactionCount
            : context.Profile.UseBatchEntityResolutionSnapshots
                ? IntegratedSnapshotReadTransactionCount
                : IntegratedLegacyReadTransactionCount;
        var expectedWrites = context.Profile.UseCoalescedPersistenceTransactions
            ? IntegratedCoalescedWriteTransactionCount
            : IntegratedLegacyWriteTransactionCount;
        var countersExact =
            context.Turn.Counter("llm.calls") == IntegratedOwnerCount &&
            calls == IntegratedOwnerCount &&
            context.Turn.Counter("llm.unified.calls") == 0 &&
            retries == 0 &&
            context.Turn.Counter("store.messages") == IntegratedMessageCount &&
            context.Turn.Counter("embed.requests") == IntegratedEmbeddingRequestCount &&
            context.Turn.Counter("embed.items") == IntegratedEmbeddingItemCount &&
            context.Turn.SpanCounts.GetValueOrDefault("provider.embedding") ==
                IntegratedEmbeddingRequestCount &&
            context.Turn.SpanCounts.GetValueOrDefault("provider.llm.unified_batch") ==
                IntegratedOwnerCount &&
            context.Turn.SpanCounts.GetValueOrDefault("memory.extract.unified_batch") ==
                IntegratedOwnerCount &&
            context.Turn.Counter("neo4j.queries") == expectedQueries &&
            context.Turn.Counter("neo4j.tx.read") == expectedReads &&
            context.Turn.Counter("neo4j.tx.write") == expectedWrites &&
            context.Turn.Counter("persist.entities") == IntegratedSourceSessionCount * 2 &&
            context.Turn.Counter("persist.facts") == IntegratedSourceSessionCount &&
            context.Turn.Counter("persist.preferences") == IntegratedSourceSessionCount &&
            context.Turn.Counter("persist.relationships") == IntegratedSourceSessionCount &&
            context.Turn.SpanCounts.GetValueOrDefault("memory.persist.total") ==
                IntegratedSourceSessionCount;

        if (!outputsExact ||
            !orderExact ||
            !countersExact ||
            raw.MaxConcurrency != workers ||
            extracted.MaxConcurrency != workers ||
            extracted.Results.Any(result =>
                result.Plan.BatchCount != 1 ||
                result.Plan.SourceSessionCount != IntegratedSessionsPerOwner) ||
            (context.Profile.MaxConnectionPoolSize != 16 &&
                !context.Profile.PoolSizeExplicitlyConfigured))
        {
            throw new InvalidOperationException(
                $"PERF-W-12-X{workers:D2} integrated contract failed (outputs/order=" +
                $"{outputsExact}/{orderExact}; raw/extract concurrency=" +
                $"{raw.MaxConcurrency}/{extracted.MaxConcurrency}, expected {workers}/{workers}; " +
                $"plan batches/sessions={context.Turn.Counter("integrated.plan_batches")}/" +
                $"{context.Turn.Counter("integrated.plan_sessions")}, expected 10/40; " +
                $"llm/batch/retries={context.Turn.Counter("llm.calls")}/{calls}/{retries}, " +
                "expected 10/10/0; " +
                $"stored={context.Turn.Counter("store.messages")}/{IntegratedMessageCount}; " +
                $"embed requests/items={context.Turn.Counter("embed.requests")}/" +
                $"{context.Turn.Counter("embed.items")}, expected " +
                $"{IntegratedEmbeddingRequestCount}/{IntegratedEmbeddingItemCount}; " +
                $"queries/read/write={context.Turn.Counter("neo4j.queries")}/" +
                $"{context.Turn.Counter("neo4j.tx.read")}/" +
                $"{context.Turn.Counter("neo4j.tx.write")}, expected " +
                $"{expectedQueries}/{expectedReads}/{expectedWrites}).");
        }
    }

    private static async Task<IntegratedOwnerInput> StoreIntegratedOwnerAsync(
        ScenarioContext context,
        int workers,
        int owner,
        CancellationToken cancellationToken)
    {
        var messages = Enumerable.Range(0, IntegratedSessionsPerOwner)
            .SelectMany(session => Enumerable.Range(0, IntegratedMessagesPerSession)
                .Select(message => IntegratedMessage(
                    workers,
                    context.Phase,
                    context.Iteration,
                    owner,
                    session,
                    message)))
            .ToArray();

        await using var scope = context.Profile.Services.CreateAsyncScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var startedAt = Stopwatch.GetTimestamp();
        var stored = await memory.AddMessagesAsync(messages, cancellationToken).ConfigureAwait(false);
        var durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        if (stored.Count != messages.Length ||
            stored.Where((message, index) =>
                message.MessageId != messages[index].MessageId ||
                message.Embedding is not { Length: > 0 } embedding ||
                embedding.Length != context.Profile.Dimensions).Any())
        {
            throw new InvalidOperationException(
                $"Integrated owner {owner} did not store all exact embedded source messages.");
        }

        var chronologicalRequests = messages
            .GroupBy(message => message.SessionId, StringComparer.Ordinal)
            .Select(group => new ExtractionRequest
            {
                Messages = group.OrderBy(message => message.TimestampUtc).ToArray(),
                SessionId = group.Key,
                UserId = IntegratedOwnerId(workers, context.Phase, context.Iteration, owner),
                TypesToExtract = ExtractionTypes.All,
            })
            .OrderBy(request => request.Messages[0].TimestampUtc)
            .ToArray();

        return new IntegratedOwnerInput(
            owner,
            messages,
            chronologicalRequests,
            chronologicalRequests.AsEnumerable().Reverse().ToArray(),
            durationMs);
    }

    private static async Task<IntegratedOwnerResult> ExtractIntegratedOwnerAsync(
        ScenarioContext context,
        IntegratedOwnerInput input,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Profile.Services.CreateAsyncScope();
        var planner = scope.ServiceProvider
            .GetServices<IMultiSessionUnifiedMemoryExtractor>()
            .Single(extractor => extractor.IsEnabled);
        var plan = planner.Plan(
            input.ChronologicalRequests,
            IntegratedSessionsPerOwner,
            IntegratedBatchTokenBudget);
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
        var startedAt = Stopwatch.GetTimestamp();
        var results = await pipeline.ExtractBatchAsync(
            input.ExecutionRequests,
            IntegratedSessionsPerOwner,
            IntegratedBatchTokenBudget,
            cancellationToken).ConfigureAwait(false);
        var durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        var plannedSessions = plan.Batches.SelectMany(batch => batch.SourceSessionIds);
        var returnedSessions = results.Select(result =>
            result.Metadata.TryGetValue("sessionId", out var value) ? value as string : null);
        if (!returnedSessions.SequenceEqual(plannedSessions, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"Integrated owner {input.Owner} execution did not match its preflight partition.");

        return new IntegratedOwnerResult(input.Owner, plan, results, durationMs);
    }

    private static async Task VerifyIntegratedColdBuildAsync(
        ScenarioVerificationContext context,
        int workers)
    {
        for (var owner = 0; owner < IntegratedOwnerCount; owner++)
        {
            var ownerId = IntegratedOwnerId(workers, context.Phase, context.Iteration, owner);
            for (var session = 0; session < IntegratedSessionsPerOwner; session++)
            {
                var unit = owner * IntegratedSessionsPerOwner + session;
                var sessionId = IntegratedSessionId(
                    workers,
                    context.Phase,
                    context.Iteration,
                    unit);
                var messageIds = Enumerable.Range(0, IntegratedMessagesPerSession)
                    .Select(message => $"{sessionId}-message-{message:D2}")
                    .ToArray();
                const string verifyCypher = """
                    CALL {
                        MATCH (m:Message {session_id: $sessionId})
                        RETURN count(m) AS messages,
                               count(CASE WHEN size(m.embedding) = $dimensions THEN 1 END)
                                   AS messageVectors
                    }
                    CALL {
                        MATCH (e:Entity {owner_id: $ownerId})-[:EXTRACTED_FROM]->
                              (:Message {session_id: $sessionId})
                        RETURN count(DISTINCT e) AS entities, count(*) AS entityProvenance
                    }
                    CALL {
                        MATCH (f:Fact {owner_id: $ownerId})-[:EXTRACTED_FROM]->
                              (:Message {session_id: $sessionId})
                        RETURN count(DISTINCT f) AS facts, count(*) AS factProvenance
                    }
                    CALL {
                        MATCH (p:Preference {owner_id: $ownerId})-[:EXTRACTED_FROM]->
                              (:Message {session_id: $sessionId})
                        RETURN count(DISTINCT p) AS preferences, count(*) AS preferenceProvenance
                    }
                    CALL {
                        MATCH (source:Entity)-[r:RELATED_TO]->(target:Entity)
                        WHERE r.owner_id = $ownerId
                          AND source.owner_id = $ownerId
                          AND target.owner_id = $ownerId
                          AND r.relation_type = 'WORKS_AT'
                          AND all(messageId IN $messageIds
                              WHERE messageId IN coalesce(r.source_message_ids, []))
                          AND EXISTS {
                              MATCH (source)-[:EXTRACTED_FROM]->
                                    (:Message {session_id: $sessionId})
                          }
                        RETURN count(DISTINCT r) AS relationships
                    }
                    CALL {
                        MATCH (source:Entity)-[r:RELATED_TO]->(target:Entity)
                        WHERE r.owner_id = $ownerId
                          AND (source.owner_id <> $ownerId OR target.owner_id <> $ownerId)
                        RETURN count(r) AS crossOwnerEdges
                    }
                    RETURN messages, messageVectors, entities, facts, preferences, relationships,
                           entityProvenance + factProvenance + preferenceProvenance AS provenance,
                           crossOwnerEdges
                    """;

                await using var sessionHandle = context.Profile.Driver.AsyncSession();
                var cursor = await sessionHandle.RunAsync(
                    verifyCypher,
                    new
                    {
                        sessionId,
                        ownerId,
                        messageIds,
                        dimensions = context.Profile.Dimensions,
                    }).ConfigureAwait(false);
                var record = await cursor.SingleAsync().ConfigureAwait(false);
                var exact =
                    record["messages"].As<long>() == IntegratedMessagesPerSession &&
                    record["messageVectors"].As<long>() == IntegratedMessagesPerSession &&
                    record["entities"].As<long>() == 2 &&
                    record["facts"].As<long>() == 1 &&
                    record["preferences"].As<long>() == 1 &&
                    record["relationships"].As<long>() == 1 &&
                    record["provenance"].As<long>() == 4L * IntegratedMessagesPerSession &&
                    record["crossOwnerEdges"].As<long>() == 0;
                if (!exact)
                    throw new InvalidOperationException(
                        $"PERF-W-12-X{workers:D2} graph/provenance/isolation failed for source " +
                        $"session {unit}.");
            }

            var sessionIds = Enumerable.Range(0, IntegratedSessionsPerOwner)
                .Select(session => IntegratedSessionId(
                    workers,
                    context.Phase,
                    context.Iteration,
                    owner * IntegratedSessionsPerOwner + session))
                .ToArray();
            var conversationIds = sessionIds
                .Select(sessionId => $"{sessionId}-conversation")
                .ToArray();
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
            await using var cleanupSession = context.Profile.Driver.AsyncSession();
            var cleanup = await cleanupSession.RunAsync(
                cleanupCypher,
                new { ownerId, sessionIds, conversationIds }).ConfigureAwait(false);
            var cleanupRecord = await cleanup.SingleAsync().ConfigureAwait(false);
            if (cleanupRecord["deleted"].As<long>() == 0 ||
                cleanupRecord["remaining"].As<long>() != 0)
            {
                throw new InvalidOperationException(
                    $"PERF-W-12-X{workers:D2} did not clean owner lane {owner}.");
            }
        }
    }

    private static Message IntegratedMessage(
        int workers,
        string phase,
        int iteration,
        int owner,
        int session,
        int message)
    {
        var unit = owner * IntegratedSessionsPerOwner + session;
        var sessionId = IntegratedSessionId(workers, phase, iteration, unit);
        return new Message
        {
            MessageId = $"{sessionId}-message-{message:D2}",
            ConversationId = $"{sessionId}-conversation",
            SessionId = sessionId,
            Role = "user",
            Content =
                $"LAB-X1 source {unit:D2}: Person {unit:D2} works at Company {unit:D2} and " +
                $"prefers tea. Supporting turn {message:D2}.",
            TimestampUtc = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)
                .AddMinutes(unit)
                .AddSeconds(message),
        };
    }

    private static string IntegratedSessionId(
        int workers,
        string phase,
        int iteration,
        int unit) =>
        $"perf-w12-x{workers:D2}-{phase}-{iteration}-session-{unit:D2}";

    private static string IntegratedOwnerId(
        int workers,
        string phase,
        int iteration,
        int owner) =>
        $"perf-w12-x{workers:D2}-{phase}-{iteration}-owner-{owner:D2}";

    private sealed record IntegratedOwnerInput(
        int Owner,
        IReadOnlyList<Message> Messages,
        IReadOnlyList<ExtractionRequest> ChronologicalRequests,
        IReadOnlyList<ExtractionRequest> ExecutionRequests,
        double DurationMs);

    private sealed record IntegratedOwnerResult(
        int Owner,
        MultiSessionExtractionPlan Plan,
        IReadOnlyList<ExtractionResult> Results,
        double DurationMs);
}
