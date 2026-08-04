using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;
using Neo4j.Driver;

namespace AgentMemory.Cli.Perf;

public static partial class PerfScenarios
{
    private const int CapacityBaseOwnerCount = 10;
    private const int CapacityBaseSessionsPerOwner = 4;
    private const int CapacityMessagesPerSession = 12;
    private const int CapacityWorkers = 10;
    private const int CapacityBatchTokenBudget = 100_000;

    private static async Task RunNeo4jCapacityAsync(
        ScenarioContext context,
        string axis,
        int factor)
    {
        var workload = CapacityWorkload.Create(axis, factor);
        await using var telemetry = await Neo4jResourceTelemetry
            .StartAsync(context.Profile, context.Turn, context.CancellationToken)
            .ConfigureAwait(false);

        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var processorTimeBefore = process.TotalProcessorTime;
        var totalStartedAt = Stopwatch.GetTimestamp();

        var rawWork = Enumerable.Range(0, workload.OwnerCount)
            .Select(owner => (Func<CancellationToken, Task<CapacityOwnerInput>>)(token =>
                StoreCapacityOwnerAsync(context, workload, owner, token)))
            .ToArray();
        var rawStartedAt = Stopwatch.GetTimestamp();
        var raw = await BoundedWorkScheduler
            .RunAsync(rawWork, workload.Workers, context.CancellationToken)
            .ConfigureAwait(false);
        var rawWaveMs = Stopwatch.GetElapsedTime(rawStartedAt).TotalMilliseconds;
        context.Turn.Add("store.messages", raw.Results.Sum(result => result.Messages.Count));

        var extractionWork = raw.Results
            .Select(input => (Func<CancellationToken, Task<CapacityOwnerResult>>)(token =>
                ExtractCapacityOwnerAsync(context, workload, input, token)))
            .ToArray();
        var extractionStartedAt = Stopwatch.GetTimestamp();
        var extracted = await BoundedWorkScheduler
            .RunAsync(extractionWork, workload.Workers, context.CancellationToken)
            .ConfigureAwait(false);
        var extractionWaveMs = Stopwatch.GetElapsedTime(extractionStartedAt).TotalMilliseconds;
        var totalWaveMs = Stopwatch.GetElapsedTime(totalStartedAt).TotalMilliseconds;

        process.Refresh();
        var processorTimeMs = (process.TotalProcessorTime - processorTimeBefore).TotalMilliseconds;

        context.Turn.Add("capacity.factor", workload.Factor);
        context.Turn.Add("capacity.axis.width", workload.Axis == "width" ? 1 : 0);
        context.Turn.Add("capacity.axis.depth", workload.Axis == "depth" ? 1 : 0);
        context.Turn.Add("capacity.owners", workload.OwnerCount);
        context.Turn.Add("capacity.source_sessions", workload.SourceSessionCount);
        context.Turn.Add("capacity.messages", workload.MessageCount);
        context.Turn.Add("capacity.workers", workload.Workers);
        context.Turn.Add("capacity.raw.max_concurrency", raw.MaxConcurrency);
        context.Turn.Add("capacity.extract.max_concurrency", extracted.MaxConcurrency);
        context.Turn.Add(
            "capacity.plan_batches",
            extracted.Results.Sum(result => result.Plan.BatchCount));
        context.Turn.Add(
            "capacity.plan_sessions",
            extracted.Results.Sum(result => result.Plan.SourceSessionCount));
        context.Turn.RecordSample("capacity.raw_wave_ms", rawWaveMs);
        context.Turn.RecordSample("capacity.extract_wave_ms", extractionWaveMs);
        context.Turn.RecordSample("capacity.total_wave_ms", totalWaveMs);
        context.Turn.RecordSample("capacity.process_cpu_ms", processorTimeMs);
        foreach (var owner in raw.Results)
            context.Turn.RecordSample("capacity.owner_raw_ms", owner.DurationMs);
        foreach (var owner in extracted.Results)
            context.Turn.RecordSample("capacity.owner_extract_ms", owner.DurationMs);

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
                result.SourceMessageIds.Count == workload.MessagesPerSession);
        var orderExact = returnedSessions.SequenceEqual(expectedSessions, StringComparer.Ordinal);
        var calls = context.Turn.Counter("llm.unified_batch.calls");
        var retries = Math.Max(0, calls - workload.OwnerCount);
        context.Turn.Add("llm.unified_batch.retries", retries);

        await telemetry.DisposeAsync().ConfigureAwait(false);

        var expectedEmbeddingRequests = workload.OwnerCount + 3L * workload.SourceSessionCount;
        var expectedEmbeddingItems = workload.MessageCount + 6L * workload.SourceSessionCount;
        var legacyQueries = workload.MessageCount + workload.OwnerCount + 11L * workload.SourceSessionCount;
        var legacyReads = 3L * workload.SourceSessionCount;
        var savedCandidateReads = context.Profile.UseBatchEntityResolutionSnapshots
            ? 2L * (workload.SourceSessionCount - workload.OwnerCount)
            : 0L;
        var expectedQueries = legacyQueries - savedCandidateReads;
        var expectedReads = legacyReads - savedCandidateReads;
        var expectedWrites = workload.OwnerCount + 6L * workload.SourceSessionCount;
        var samples = context.Turn.Samples;
        var telemetryExact =
            context.Turn.Counter("neo4j.telemetry.docker_samples") > 0 &&
            context.Turn.Counter("neo4j.telemetry.neo4j_samples") > 0 &&
            context.Turn.Counter("neo4j.telemetry.docker_parse_errors") == 0 &&
            context.Turn.Counter("neo4j.telemetry.docker_errors") == 0 &&
            context.Turn.Counter("neo4j.telemetry.neo4j_errors") == 0 &&
            samples.ContainsKey("neo4j.container.cpu_capacity_percent") &&
            samples.ContainsKey("neo4j.container.memory_used_bytes") &&
            samples.ContainsKey("neo4j.container.block_read_bytes") &&
            samples.ContainsKey("neo4j.jvm.heap_used_bytes") &&
            samples.ContainsKey("neo4j.transactions.active") &&
            samples.ContainsKey("neo4j.page_cache.configured_bytes") &&
            samples.ContainsKey("neo4j.transaction_entry_ms_est");
        var countersExact =
            context.Turn.Counter("llm.calls") == workload.OwnerCount &&
            calls == workload.OwnerCount &&
            context.Turn.Counter("llm.unified.calls") == 0 &&
            retries == 0 &&
            context.Turn.Counter("store.messages") == workload.MessageCount &&
            context.Turn.Counter("embed.requests") == expectedEmbeddingRequests &&
            context.Turn.Counter("embed.items") == expectedEmbeddingItems &&
            context.Turn.SpanCounts.GetValueOrDefault("provider.embedding") == expectedEmbeddingRequests &&
            context.Turn.SpanCounts.GetValueOrDefault("provider.llm.unified_batch") == workload.OwnerCount &&
            context.Turn.SpanCounts.GetValueOrDefault("memory.extract.unified_batch") == workload.OwnerCount &&
            context.Turn.Counter("neo4j.queries") == expectedQueries &&
            context.Turn.Counter("neo4j.tx.read") == expectedReads &&
            context.Turn.Counter("neo4j.tx.write") == expectedWrites &&
            context.Turn.Counter("persist.entities") == workload.SourceSessionCount * 2L &&
            context.Turn.Counter("persist.facts") == workload.SourceSessionCount &&
            context.Turn.Counter("persist.preferences") == workload.SourceSessionCount &&
            context.Turn.Counter("persist.relationships") == workload.SourceSessionCount &&
            context.Turn.SpanCounts.GetValueOrDefault("memory.persist.total") == workload.SourceSessionCount;

        if (!outputsExact ||
            !orderExact ||
            !countersExact ||
            !telemetryExact ||
            raw.MaxConcurrency != workload.Workers ||
            extracted.MaxConcurrency != workload.Workers ||
            extracted.Results.Any(result =>
                result.Plan.BatchCount != 1 ||
                result.Plan.SourceSessionCount != workload.SessionsPerOwner) ||
            context.Profile.MaxConnectionPoolSize != 16)
        {
            throw new InvalidOperationException(
                $"{workload.ScenarioId} capacity contract failed (outputs/order/telemetry=" +
                $"{outputsExact}/{orderExact}/{telemetryExact}; raw/extract concurrency=" +
                $"{raw.MaxConcurrency}/{extracted.MaxConcurrency}, expected " +
                $"{workload.Workers}/{workload.Workers}; plan batches/sessions=" +
                $"{context.Turn.Counter("capacity.plan_batches")}/" +
                $"{context.Turn.Counter("capacity.plan_sessions")}, expected " +
                $"{workload.OwnerCount}/{workload.SourceSessionCount}; llm/batch/retries=" +
                $"{context.Turn.Counter("llm.calls")}/{calls}/{retries}, expected " +
                $"{workload.OwnerCount}/{workload.OwnerCount}/0; stored=" +
                $"{context.Turn.Counter("store.messages")}/{workload.MessageCount}; " +
                $"embed requests/items={context.Turn.Counter("embed.requests")}/" +
                $"{context.Turn.Counter("embed.items")}, expected " +
                $"{expectedEmbeddingRequests}/{expectedEmbeddingItems}; queries/read/write=" +
                $"{context.Turn.Counter("neo4j.queries")}/" +
                $"{context.Turn.Counter("neo4j.tx.read")}/" +
                $"{context.Turn.Counter("neo4j.tx.write")}, expected " +
                $"{expectedQueries}/{expectedReads}/{expectedWrites}).");
        }
    }

    private static async Task<CapacityOwnerInput> StoreCapacityOwnerAsync(
        ScenarioContext context,
        CapacityWorkload workload,
        int owner,
        CancellationToken cancellationToken)
    {
        var messages = Enumerable.Range(0, workload.SessionsPerOwner)
            .SelectMany(session => Enumerable.Range(0, workload.MessagesPerSession)
                .Select(message => CapacityMessage(
                    workload,
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
                $"{workload.ScenarioId} owner {owner} did not store exact embedded source messages.");
        }

        var chronologicalRequests = messages
            .GroupBy(message => message.SessionId, StringComparer.Ordinal)
            .Select(group => new ExtractionRequest
            {
                Messages = group.OrderBy(message => message.TimestampUtc).ToArray(),
                SessionId = group.Key,
                UserId = CapacityOwnerId(workload, context.Phase, context.Iteration, owner),
                TypesToExtract = ExtractionTypes.All,
            })
            .OrderBy(request => request.Messages[0].TimestampUtc)
            .ToArray();

        return new CapacityOwnerInput(
            owner,
            messages,
            chronologicalRequests,
            chronologicalRequests.AsEnumerable().Reverse().ToArray(),
            durationMs);
    }

    private static async Task<CapacityOwnerResult> ExtractCapacityOwnerAsync(
        ScenarioContext context,
        CapacityWorkload workload,
        CapacityOwnerInput input,
        CancellationToken cancellationToken)
    {
        await using var scope = context.Profile.Services.CreateAsyncScope();
        var planner = scope.ServiceProvider
            .GetServices<IMultiSessionUnifiedMemoryExtractor>()
            .Single(extractor => extractor.IsEnabled);
        var plan = planner.Plan(
            input.ChronologicalRequests,
            workload.SessionsPerOwner,
            CapacityBatchTokenBudget);
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
        var startedAt = Stopwatch.GetTimestamp();
        var results = await pipeline.ExtractBatchAsync(
            input.ExecutionRequests,
            workload.SessionsPerOwner,
            CapacityBatchTokenBudget,
            cancellationToken).ConfigureAwait(false);
        var durationMs = Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

        var plannedSessions = plan.Batches.SelectMany(batch => batch.SourceSessionIds);
        var returnedSessions = results.Select(result =>
            result.Metadata.TryGetValue("sessionId", out var value) ? value as string : null);
        if (!returnedSessions.SequenceEqual(plannedSessions, StringComparer.Ordinal))
            throw new InvalidOperationException(
                $"{workload.ScenarioId} owner {input.Owner} did not match its preflight order.");

        return new CapacityOwnerResult(input.Owner, plan, results, durationMs);
    }

    private static async Task VerifyNeo4jCapacityAsync(
        ScenarioVerificationContext context,
        string axis,
        int factor)
    {
        var workload = CapacityWorkload.Create(axis, factor);
        for (var owner = 0; owner < workload.OwnerCount; owner++)
        {
            var ownerId = CapacityOwnerId(workload, context.Phase, context.Iteration, owner);
            var sessionIds = Enumerable.Range(0, workload.SessionsPerOwner)
                .Select(session => CapacitySessionId(
                    workload,
                    context.Phase,
                    context.Iteration,
                    owner * workload.SessionsPerOwner + session))
                .ToArray();
            const string verifyCypher = """
                UNWIND $sessionIds AS sessionId
                CALL {
                    WITH sessionId
                    MATCH (m:Message {session_id: sessionId})
                    RETURN count(m) AS messages,
                           count(CASE WHEN size(m.embedding) = $dimensions THEN 1 END) AS messageVectors
                }
                CALL {
                    WITH sessionId
                    MATCH (e:Entity {owner_id: $ownerId})-[:EXTRACTED_FROM]->
                          (:Message {session_id: sessionId})
                    RETURN count(DISTINCT e) AS entities, count(*) AS entityProvenance
                }
                CALL {
                    WITH sessionId
                    MATCH (f:Fact {owner_id: $ownerId})-[:EXTRACTED_FROM]->
                          (:Message {session_id: sessionId})
                    RETURN count(DISTINCT f) AS facts, count(*) AS factProvenance
                }
                CALL {
                    WITH sessionId
                    MATCH (p:Preference {owner_id: $ownerId})-[:EXTRACTED_FROM]->
                          (:Message {session_id: sessionId})
                    RETURN count(DISTINCT p) AS preferences, count(*) AS preferenceProvenance
                }
                CALL {
                    WITH sessionId
                    MATCH (source:Entity)-[r:RELATED_TO]->(target:Entity)
                    WHERE r.owner_id = $ownerId
                      AND source.owner_id = $ownerId
                      AND target.owner_id = $ownerId
                      AND r.relation_type = 'WORKS_AT'
                      AND EXISTS {
                          MATCH (source)-[:EXTRACTED_FROM]->(:Message {session_id: sessionId})
                      }
                    RETURN count(DISTINCT r) AS relationships
                }
                RETURN sessionId, messages, messageVectors, entities, facts, preferences,
                       relationships, entityProvenance + factProvenance + preferenceProvenance AS provenance
                ORDER BY sessionId
                """;

            await using var sessionHandle = context.Profile.Driver.AsyncSession();
            var cursor = await sessionHandle.RunAsync(
                verifyCypher,
                new { sessionIds, ownerId, dimensions = context.Profile.Dimensions }).ConfigureAwait(false);
            var records = await cursor.ToListAsync().ConfigureAwait(false);
            var exact = records.Count == workload.SessionsPerOwner && records.All(record =>
                record["messages"].As<long>() == workload.MessagesPerSession &&
                record["messageVectors"].As<long>() == workload.MessagesPerSession &&
                record["entities"].As<long>() == 2 &&
                record["facts"].As<long>() == 1 &&
                record["preferences"].As<long>() == 1 &&
                record["relationships"].As<long>() == 1 &&
                record["provenance"].As<long>() == 4L * workload.MessagesPerSession);
            if (!exact)
                throw new InvalidOperationException(
                    $"{workload.ScenarioId} graph/provenance verification failed for owner {owner}.");

            const string isolationCypher = """
                MATCH (source:Entity)-[r:RELATED_TO]->(target:Entity)
                WHERE r.owner_id = $ownerId
                  AND (source.owner_id <> $ownerId OR target.owner_id <> $ownerId)
                RETURN count(r) AS crossOwnerEdges
                """;
            var isolationCursor = await sessionHandle.RunAsync(isolationCypher, new { ownerId })
                .ConfigureAwait(false);
            var isolation = await isolationCursor.SingleAsync().ConfigureAwait(false);
            if (isolation["crossOwnerEdges"].As<long>() != 0)
                throw new InvalidOperationException(
                    $"{workload.ScenarioId} owner isolation failed for owner {owner}.");

            var conversationIds = sessionIds.Select(id => $"{id}-conversation").ToArray();
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
            var cleanupCursor = await sessionHandle.RunAsync(
                cleanupCypher,
                new { ownerId, sessionIds, conversationIds }).ConfigureAwait(false);
            var cleanup = await cleanupCursor.SingleAsync().ConfigureAwait(false);
            if (cleanup["deleted"].As<long>() == 0 || cleanup["remaining"].As<long>() != 0)
                throw new InvalidOperationException(
                    $"{workload.ScenarioId} did not clean owner lane {owner}.");
        }
    }

    private static Message CapacityMessage(
        CapacityWorkload workload,
        string phase,
        int iteration,
        int owner,
        int session,
        int message)
    {
        var unit = owner * workload.SessionsPerOwner + session;
        var sessionId = CapacitySessionId(workload, phase, iteration, unit);
        return new Message
        {
            MessageId = $"{sessionId}-message-{message:D2}",
            ConversationId = $"{sessionId}-conversation",
            SessionId = sessionId,
            Role = "user",
            Content =
                $"LAB-N1 source {unit:D3}: Person {unit:D3} works at Company {unit:D3} and " +
                $"prefers tea. Supporting turn {message:D2}.",
            TimestampUtc = new DateTimeOffset(2026, 4, 1, 12, 0, 0, TimeSpan.Zero)
                .AddMinutes(unit)
                .AddSeconds(message),
        };
    }

    private static string CapacitySessionId(
        CapacityWorkload workload,
        string phase,
        int iteration,
        int unit) =>
        $"perf-w13-{workload.Axis[0]}{workload.Factor:D2}-{phase}-{iteration}-session-{unit:D3}";

    private static string CapacityOwnerId(
        CapacityWorkload workload,
        string phase,
        int iteration,
        int owner) =>
        $"perf-w13-{workload.Axis[0]}{workload.Factor:D2}-{phase}-{iteration}-owner-{owner:D3}";

    private sealed record CapacityOwnerInput(
        int Owner,
        IReadOnlyList<Message> Messages,
        IReadOnlyList<ExtractionRequest> ChronologicalRequests,
        IReadOnlyList<ExtractionRequest> ExecutionRequests,
        double DurationMs);

    private sealed record CapacityOwnerResult(
        int Owner,
        MultiSessionExtractionPlan Plan,
        IReadOnlyList<ExtractionResult> Results,
        double DurationMs);

    private sealed record CapacityWorkload(
        string ScenarioId,
        string Axis,
        int Factor,
        int OwnerCount,
        int SessionsPerOwner,
        int MessagesPerSession,
        int Workers)
    {
        public int SourceSessionCount => OwnerCount * SessionsPerOwner;
        public int MessageCount => SourceSessionCount * MessagesPerSession;

        public static CapacityWorkload Create(string axis, int factor)
        {
            if (factor is not (1 or 2 or 4 or 8))
                throw new ArgumentOutOfRangeException(nameof(factor));
            return axis switch
            {
                "width" => new(
                    $"PERF-W-13-W{factor:D2}", axis, factor,
                    CapacityBaseOwnerCount * factor,
                    CapacityBaseSessionsPerOwner,
                    CapacityMessagesPerSession,
                    CapacityWorkers),
                "depth" => new(
                    $"PERF-W-13-D{factor:D2}", axis, factor,
                    CapacityBaseOwnerCount,
                    CapacityBaseSessionsPerOwner * factor,
                    CapacityMessagesPerSession,
                    CapacityWorkers),
                _ => throw new ArgumentOutOfRangeException(nameof(axis)),
            };
        }
    }
}
