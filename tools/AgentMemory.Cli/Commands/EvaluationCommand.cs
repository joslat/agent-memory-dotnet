using System.Diagnostics;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.Options;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// Runs deterministic memory-layer quality and performance checks against a live Neo4j-backed store.
/// This intentionally evaluates storage/retrieval behavior, not model answers or context assembly quality.
/// </summary>
public sealed class EvaluationCommand(
    ISchemaBootstrapper bootstrapper,
    INeo4jTransactionRunner txRunner,
    IOptions<Neo4jOptions> neo4jOptions,
    IShortTermMemoryService shortTerm,
    ILongTermMemoryService longTerm,
    IReasoningMemoryService reasoning,
    IMemoryHistoryService history,
    IToolCallRepository toolCalls,
    TextWriter output)
{
    private readonly List<OperationSample> _samples = new();
    private readonly Neo4jOptions _neo4j = neo4jOptions.Value;

    public async Task<int> ExecuteAsync(string? outputPath, string? iterationsValue, string? ownerValue)
    {
        if (!TryParseIterations(iterationsValue, out var iterations))
        {
            output.WriteLine("error: evaluate --iterations must be an integer between 1 and 50.");
            return 1;
        }

        var generatedAt = DateTimeOffset.UtcNow;
        var runId = $"memory-eval-{generatedAt:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..38];
        var ownerPrefix = string.IsNullOrWhiteSpace(ownerValue) ? runId : ownerValue.Trim();
        var destination = ResolveOutputPath(outputPath, generatedAt);
        var scenarios = new List<EvaluationScenarioResult>();

        await MeasureAsync("schema.bootstrap", () => bootstrapper.BootstrapAsync()).ConfigureAwait(false);
        await MeasureAsync("schema.await_indexes", async () =>
        {
            await txRunner.WriteAsync(async runner =>
            {
                await runner.RunAsync("CALL db.awaitIndexes(60)").ConfigureAwait(false);
            }).ConfigureAwait(false);
        }).ConfigureAwait(false);

        for (var iteration = 1; iteration <= iterations; iteration++)
        {
            var ownerId = $"{ownerPrefix}-i{iteration}";
            scenarios.Add(await RunScenarioAsync("MQ-001", "short-term persistence", iteration, ownerId, RunShortTermRoundTripAsync).ConfigureAwait(false));
            scenarios.Add(await RunScenarioAsync("MQ-002", "long-term round-trip and owner isolation", iteration, ownerId, RunLongTermRoundTripAsync).ConfigureAwait(false));
            scenarios.Add(await RunScenarioAsync("MQ-003", "relationship traversal and touched-entity provenance", iteration, ownerId, RunRelationshipAndTouchedEntitiesAsync).ConfigureAwait(false));
            scenarios.Add(await RunScenarioAsync("MQ-004", "reasoning trace, steps, and tool calls", iteration, ownerId, RunReasoningTraceAsync).ConfigureAwait(false));
            scenarios.Add(await RunScenarioAsync("MQ-005", "temporal history and supersession", iteration, ownerId, RunTemporalHistoryAsync).ConfigureAwait(false));
            scenarios.Add(await RunScenarioAsync("MQ-006", "fixed-embedding retrieval quality", iteration, ownerId, RunRetrievalFixtureAsync).ConfigureAwait(false));
        }

        var operationMetrics = BuildOperationMetrics(_samples);
        var summary = BuildSummary(scenarios, operationMetrics);
        var report = new EvaluationReport(
            GeneratedAtUtc: generatedAt,
            RunId: runId,
            OwnerPrefix: ownerPrefix,
            Iterations: iterations,
            Database: _neo4j.Database,
            EmbeddingDimensions: _neo4j.EmbeddingDimensions,
            Summary: summary,
            Scenarios: scenarios,
            Operations: operationMetrics);

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await File.WriteAllTextAsync(destination, json).ConfigureAwait(false);

        output.WriteLine($"evaluation: {summary.PassedScenarios}/{summary.TotalScenarios} scenarios passed; owner_leaks={summary.OwnerLeakCount}; recall_at_1={summary.RecallAt1:0.###}; p95={summary.P95Ms:0.###} ms.");
        output.WriteLine($"evaluation report: {destination}");
        return summary.FailedScenarios == 0 ? 0 : 1;
    }

    private async Task<IReadOnlyDictionary<string, double>> RunShortTermRoundTripAsync(string ownerId)
    {
        var sessionId = Id("session");
        var conversationId = Id("conversation");

        await MeasureAsync("shortterm.add_conversation", () => shortTerm.AddConversationAsync(
            conversationId,
            sessionId,
            userId: ownerId,
            metadata: new Dictionary<string, object> { ["evaluation"] = true })).ConfigureAwait(false);

        for (var i = 0; i < 5; i++)
        {
            await MeasureAsync("shortterm.add_message", () => shortTerm.AddMessageAsync(new Message
            {
                MessageId = Id("message"),
                ConversationId = conversationId,
                SessionId = sessionId,
                Role = i % 2 == 0 ? "user" : "assistant",
                Content = $"evaluation message {i}",
                TimestampUtc = DateTimeOffset.UtcNow.AddSeconds(i),
                Metadata = new Dictionary<string, object> { ["owner"] = ownerId }
            })).ConfigureAwait(false);
        }

        var recent = await MeasureAsync("shortterm.get_recent_messages", () => shortTerm.GetRecentMessagesAsync(sessionId, limit: 5)).ConfigureAwait(false);
        var all = await MeasureAsync("shortterm.get_all_session_messages", () => shortTerm.GetAllSessionMessagesAsync(sessionId)).ConfigureAwait(false);
        Require(recent.Count == 5, "recent message count mismatch");
        Require(all.Count == 5, "all-session message count mismatch");

        return new Dictionary<string, double>
        {
            ["messages_written"] = 5,
            ["recent_count"] = recent.Count,
            ["all_count"] = all.Count,
        };
    }

    private async Task<IReadOnlyDictionary<string, double>> RunLongTermRoundTripAsync(string ownerId)
    {
        var ownerOnly = MemoryScope.For(ownerId, includeShared: false);
        var ownerWithShared = MemoryScope.For(ownerId);
        var otherOwner = $"{ownerId}-other";
        var otherScope = MemoryScope.For(otherOwner, includeShared: false);

        var entity = await MeasureAsync("longterm.add_entity", () => longTerm.AddEntityAsync(NewEntity("Eval Alice", "Person", ownerId, UnitVector(0)))).ConfigureAwait(false);
        var otherEntity = await MeasureAsync("longterm.add_entity", () => longTerm.AddEntityAsync(NewEntity("Eval Other Secret", "Person", otherOwner, UnitVector(1)))).ConfigureAwait(false);
        var shared = await MeasureAsync("longterm.add_entity", () => longTerm.AddEntityAsync(NewEntity("Eval Shared Catalog", "Object", null, UnitVector(2)))).ConfigureAwait(false);
        var factA = await MeasureAsync("longterm.add_fact", () => longTerm.AddFactAsync(NewFact("Eval Alice", "works_at", "Neo4j", ownerId, UnitVector(0)))).ConfigureAwait(false);
        var factB = await MeasureAsync("longterm.add_fact", () => longTerm.AddFactAsync(NewFact("Eval Alice", "uses", "graph memory", ownerId, UnitVector(0)))).ConfigureAwait(false);
        await MeasureAsync("longterm.add_fact", () => longTerm.AddFactAsync(NewFact("Eval Alice", "works_at", "Private Other Lab", otherOwner, UnitVector(1)))).ConfigureAwait(false);
        var preference = await MeasureAsync("longterm.add_preference", () => longTerm.AddPreferenceAsync(NewPreference("communication", "Prefers direct summaries", ownerId, UnitVector(0)))).ConfigureAwait(false);
        await MeasureAsync("longterm.add_preference", () => longTerm.AddPreferenceAsync(NewPreference("communication", "Other private preference", otherOwner, UnitVector(1)))).ConfigureAwait(false);

        var ownEntities = await MeasureAsync("longterm.get_entities_by_name", () => longTerm.GetEntitiesByNameAsync("Eval Alice", includeAliases: true, ownerOnly)).ConfigureAwait(false);
        var leakedEntity = await MeasureAsync("longterm.get_entities_by_name", () => longTerm.GetEntitiesByNameAsync("Eval Other Secret", includeAliases: true, ownerOnly)).ConfigureAwait(false);
        var sharedVisible = await MeasureAsync("longterm.get_entities_by_name", () => longTerm.GetEntitiesByNameAsync("Eval Shared Catalog", includeAliases: true, ownerWithShared)).ConfigureAwait(false);
        var sharedExcluded = await MeasureAsync("longterm.get_entities_by_name", () => longTerm.GetEntitiesByNameAsync("Eval Shared Catalog", includeAliases: true, ownerOnly)).ConfigureAwait(false);
        var facts = await MeasureAsync("longterm.get_facts_by_subject", () => longTerm.GetFactsBySubjectAsync("Eval Alice", ownerOnly)).ConfigureAwait(false);
        var otherFacts = await MeasureAsync("longterm.get_facts_by_subject", () => longTerm.GetFactsBySubjectAsync("Eval Alice", otherScope)).ConfigureAwait(false);
        var preferences = await MeasureAsync("longterm.get_preferences_by_category", () => longTerm.GetPreferencesByCategoryAsync("communication", ownerOnly)).ConfigureAwait(false);

        var leakCount = leakedEntity.Count + facts.Count(f => f.OwnerId != ownerId) + preferences.Count(p => p.OwnerId != ownerId);
        Require(ownEntities.Any(e => e.EntityId == entity.EntityId), "owner entity lookup failed");
        Require(sharedVisible.Any(e => e.EntityId == shared.EntityId), "shared entity should be visible with includeShared=true");
        Require(sharedExcluded.Count == 0, "shared entity should be excluded with includeShared=false");
        Require(facts.Any(f => f.FactId == factA.FactId) && facts.Any(f => f.FactId == factB.FactId), "owner facts missing");
        Require(preferences.Any(p => p.PreferenceId == preference.PreferenceId), "owner preference missing");
        Require(otherFacts.All(f => f.OwnerId == otherOwner), "other owner scope returned unexpected facts");
        Require(otherEntity.OwnerId == otherOwner, "other owner fixture was not stamped");
        Require(leakCount == 0, "owner isolation leak detected");

        return new Dictionary<string, double>
        {
            ["owner_leak_count"] = leakCount,
            ["entities_checked"] = ownEntities.Count + sharedVisible.Count + sharedExcluded.Count,
            ["facts_checked"] = facts.Count,
            ["preferences_checked"] = preferences.Count,
        };
    }

    private async Task<IReadOnlyDictionary<string, double>> RunRelationshipAndTouchedEntitiesAsync(string ownerId)
    {
        var ownerOnly = MemoryScope.For(ownerId, includeShared: false);
        var otherScope = MemoryScope.For($"{ownerId}-other", includeShared: false);
        var person = await MeasureAsync("longterm.add_entity", () => longTerm.AddEntityAsync(NewEntity("Eval Person", "Person", ownerId, UnitVector(0)))).ConfigureAwait(false);
        var organization = await MeasureAsync("longterm.add_entity", () => longTerm.AddEntityAsync(NewEntity("Eval Organization", "Organization", ownerId, UnitVector(1)))).ConfigureAwait(false);
        var relationship = await MeasureAsync("longterm.add_relationship", () => longTerm.AddRelationshipAsync(new Relationship
        {
            RelationshipId = Id("relationship"),
            SourceEntityId = person.EntityId,
            TargetEntityId = organization.EntityId,
            RelationshipType = "WORKS_FOR",
            Confidence = 0.95,
            OwnerId = ownerId,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        })).ConfigureAwait(false);

        var trace = await MeasureAsync("reasoning.start_trace", () => reasoning.StartTraceAsync(Id("session"), "Use related entities", taskEmbedding: UnitVector(0), ownerId: ownerId)).ConfigureAwait(false);
        var step = await MeasureAsync("reasoning.add_step", () => reasoning.AddStepAsync(trace.TraceId, 1, thought: "Inspect graph relationships")).ConfigureAwait(false);
        var touchedCount = await MeasureAsync("reasoning.record_touched_entities", () => reasoning.RecordTouchedEntitiesAsync(step.StepId, [person.EntityId, organization.EntityId])).ConfigureAwait(false);
        var touched = await MeasureAsync("reasoning.get_touched_entities", () => reasoning.GetTouchedEntitiesAsync(step.StepId)).ConfigureAwait(false);
        var outgoing = await MeasureAsync("longterm.get_entity_relationships", () => longTerm.GetEntityRelationshipsAsync(person.EntityId, ownerOnly)).ConfigureAwait(false);
        var incoming = await MeasureAsync("longterm.get_entity_relationships", () => longTerm.GetEntityRelationshipsAsync(organization.EntityId, ownerOnly)).ConfigureAwait(false);
        var leaked = await MeasureAsync("longterm.get_entity_relationships", () => longTerm.GetEntityRelationshipsAsync(person.EntityId, otherScope)).ConfigureAwait(false);

        Require(touchedCount == 2, "touched entity count mismatch");
        Require(touched.Contains(person.EntityId) && touched.Contains(organization.EntityId), "touched entity ids missing");
        Require(outgoing.Any(r => r.RelationshipId == relationship.RelationshipId), "outgoing relationship missing");
        Require(incoming.Any(r => r.RelationshipId == relationship.RelationshipId), "incoming relationship missing");
        Require(leaked.Count == 0, "relationship owner leak detected");

        return new Dictionary<string, double>
        {
            ["owner_leak_count"] = leaked.Count,
            ["relationships_checked"] = outgoing.Count + incoming.Count,
            ["touched_entities"] = touched.Count,
        };
    }

    private async Task<IReadOnlyDictionary<string, double>> RunReasoningTraceAsync(string ownerId)
    {
        var ownerOnly = MemoryScope.For(ownerId, includeShared: false);
        var sessionId = Id("session");
        var trace = await MeasureAsync("reasoning.start_trace", () => reasoning.StartTraceAsync(sessionId, "Evaluate reasoning memory", taskEmbedding: UnitVector(0), ownerId: ownerId)).ConfigureAwait(false);
        var step1 = await MeasureAsync("reasoning.add_step", () => reasoning.AddStepAsync(trace.TraceId, 1, thought: "Search entity", action: "search_entities", observation: "Found candidate")).ConfigureAwait(false);
        var step2 = await MeasureAsync("reasoning.add_step", () => reasoning.AddStepAsync(trace.TraceId, 2, thought: "Validate fact", action: "get_facts", observation: "Matched expected fact")).ConfigureAwait(false);
        var success = await MeasureAsync("reasoning.record_tool_call", () => reasoning.RecordToolCallAsync(step1.StepId, "search_entities", "{\"query\":\"Eval Alice\"}", resultJson: "{\"count\":1}", status: ToolCallStatus.Success, durationMs: 12)).ConfigureAwait(false);
        var failure = await MeasureAsync("reasoning.record_tool_call", () => reasoning.RecordToolCallAsync(step1.StepId, "search_preferences", "{\"category\":\"missing\"}", status: ToolCallStatus.Error, error: "category not found")).ConfigureAwait(false);
        await MeasureAsync("reasoning.complete_trace", () => reasoning.CompleteTraceAsync(trace.TraceId, outcome: "validated", success: true)).ConfigureAwait(false);
        await MeasureAsync("reasoning.start_trace", () => reasoning.StartTraceAsync(sessionId, "Other private trace", ownerId: $"{ownerId}-other")).ConfigureAwait(false);

        var read = await MeasureAsync("reasoning.get_trace_with_steps", () => reasoning.GetTraceWithStepsAsync(trace.TraceId)).ConfigureAwait(false);
        var calls = await MeasureAsync("reasoning.get_tool_calls_by_step", () => toolCalls.GetByStepAsync(step1.StepId)).ConfigureAwait(false);
        var ownerTraces = await MeasureAsync("reasoning.list_traces", () => reasoning.ListTracesAsync(sessionId, limit: 10, ownerOnly)).ConfigureAwait(false);

        Require(read.Trace.Success == true && read.Trace.Outcome == "validated", "completed trace state mismatch");
        Require(read.Steps.Any(s => s.StepId == step1.StepId) && read.Steps.Any(s => s.StepId == step2.StepId), "trace steps missing");
        Require(calls.Any(c => c.ToolCallId == success.ToolCallId) && calls.Any(c => c.ToolCallId == failure.ToolCallId), "tool calls missing");
        Require(ownerTraces.Count == 1 && ownerTraces[0].TraceId == trace.TraceId, "owner-scoped trace list mismatch");

        return new Dictionary<string, double>
        {
            ["owner_leak_count"] = ownerTraces.Count(t => t.OwnerId != ownerId),
            ["steps_checked"] = read.Steps.Count,
            ["tool_calls_checked"] = calls.Count,
        };
    }

    private async Task<IReadOnlyDictionary<string, double>> RunTemporalHistoryAsync(string ownerId)
    {
        var ownerOnly = MemoryScope.For(ownerId, includeShared: false);
        var loser = await MeasureAsync("longterm.add_fact", () => longTerm.AddFactAsync(NewFact("Eval Alice", "lives_in", "Paris", ownerId, UnitVector(0), ["eval-source-old"]))).ConfigureAwait(false);
        var winner = await MeasureAsync("longterm.add_fact", () => longTerm.AddFactAsync(NewFact("Eval Alice", "lives_in", "London", ownerId, UnitVector(0), ["eval-source-new"]))).ConfigureAwait(false);
        var superseded = await MeasureAsync("longterm.supersede_fact", () => longTerm.SupersedeFactAsync(loser.FactId, winner.FactId, ownerOnly)).ConfigureAwait(false);
        var records = await MeasureAsync("history.get_history", () => history.GetHistoryAsync(new MemoryHistoryQuery
        {
            Kind = MemoryHistoryKind.Fact,
            OwnerId = ownerId,
            IncludeShared = false,
            Limit = 20,
        })).ConfigureAwait(false);
        var liveOnly = await MeasureAsync("history.get_history_live_only", () => history.GetHistoryAsync(new MemoryHistoryQuery
        {
            Kind = MemoryHistoryKind.Fact,
            OwnerId = ownerId,
            IncludeShared = false,
            IncludeInvalidated = false,
            Limit = 20,
        })).ConfigureAwait(false);

        var loserRecord = records.FirstOrDefault(r => r.Id == loser.FactId);
        var winnerRecord = records.FirstOrDefault(r => r.Id == winner.FactId);
        Require(superseded, "fact supersession returned false");
        if (loserRecord is null)
        {
            throw new InvalidOperationException("loser history record missing");
        }

        if (winnerRecord is null)
        {
            throw new InvalidOperationException("winner history record missing");
        }

        Require(loserRecord.Status == MemoryHistoryStatus.Invalidated && loserRecord.ValidUntilUtc.HasValue, "loser history state missing");
        Require(loserRecord.SupersededByIds.Contains(winner.FactId), "superseded-by link missing");
        Require(loserRecord.SourceMessageIds.Contains("eval-source-old"), "source provenance missing");
        Require(winnerRecord.Status == MemoryHistoryStatus.Live, "winner history state missing");
        Require(liveOnly.All(r => r.Id != loser.FactId), "live-only history included invalidated loser");

        return new Dictionary<string, double>
        {
            ["invalidated_records_checked"] = 1,
            ["live_records_checked"] = 1,
            ["supersession_links_checked"] = loserRecord.SupersededByIds.Count,
        };
    }

    private async Task<IReadOnlyDictionary<string, double>> RunRetrievalFixtureAsync(string ownerId)
    {
        var ownerOnly = MemoryScope.For(ownerId, includeShared: false);
        var target = await MeasureAsync("longterm.add_entity", () => longTerm.AddEntityAsync(NewEntity("Eval Vector Target", "Concept", ownerId, UnitVector(0)))).ConfigureAwait(false);
        await MeasureAsync("longterm.add_entity", () => longTerm.AddEntityAsync(NewEntity("Eval Vector Distractor", "Concept", ownerId, UnitVector(1)))).ConfigureAwait(false);
        var top1 = await MeasureAsync("longterm.search_entities", () => longTerm.SearchEntitiesAsync(UnitVector(0), limit: 1, minScore: 0.0, scope: ownerOnly)).ConfigureAwait(false);
        var top5 = await MeasureAsync("longterm.search_entities", () => longTerm.SearchEntitiesAsync(UnitVector(0), limit: 5, minScore: 0.0, scope: ownerOnly)).ConfigureAwait(false);

        var rank = top5.Select((entity, index) => new { entity.EntityId, Rank = index + 1 }).FirstOrDefault(x => x.EntityId == target.EntityId)?.Rank;
        var recallAt1 = top1.Any(e => e.EntityId == target.EntityId) ? 1.0 : 0.0;
        var mrr = rank.HasValue ? 1.0 / rank.Value : 0.0;
        Require(recallAt1 == 1.0, "target entity was not top-1 for fixed embedding query");

        return new Dictionary<string, double>
        {
            ["recall_at_1"] = recallAt1,
            ["mrr"] = mrr,
            ["result_count"] = top5.Count,
        };
    }

    private async Task<EvaluationScenarioResult> RunScenarioAsync(
        string id,
        string name,
        int iteration,
        string ownerId,
        Func<string, Task<IReadOnlyDictionary<string, double>>> scenario)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var metrics = await scenario(ownerId).ConfigureAwait(false);
            sw.Stop();
            return new EvaluationScenarioResult(id, name, iteration, true, sw.Elapsed.TotalMilliseconds, metrics, null);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new EvaluationScenarioResult(id, name, iteration, false, sw.Elapsed.TotalMilliseconds, new Dictionary<string, double>(), ex.Message);
        }
    }

    private async Task MeasureAsync(string operation, Func<Task> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            _samples.Add(new OperationSample(operation, sw.Elapsed.TotalMilliseconds));
        }
    }

    private async Task<T> MeasureAsync<T>(string operation, Func<Task<T>> action)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            sw.Stop();
            _samples.Add(new OperationSample(operation, sw.Elapsed.TotalMilliseconds));
        }
    }

    private static IReadOnlyList<EvaluationOperationMetric> BuildOperationMetrics(IReadOnlyList<OperationSample> samples) =>
        samples
            .GroupBy(s => s.Operation)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g =>
            {
                var values = g.Select(s => s.DurationMs).OrderBy(v => v).ToArray();
                return new EvaluationOperationMetric(
                    Operation: g.Key,
                    Count: values.Length,
                    MinMs: values[0],
                    P50Ms: Percentile(values, 50),
                    P95Ms: Percentile(values, 95),
                    P99Ms: Percentile(values, 99),
                    MaxMs: values[^1]);
            })
            .ToList();

    private static EvaluationSummary BuildSummary(
        IReadOnlyList<EvaluationScenarioResult> scenarios,
        IReadOnlyList<EvaluationOperationMetric> operations)
    {
        var total = scenarios.Count;
        var passed = scenarios.Count(s => s.Passed);
        var ownerLeaks = scenarios.Sum(s => s.Metrics.TryGetValue("owner_leak_count", out var v) ? v : 0.0);
        var recallValues = scenarios.Select(s => s.Metrics.TryGetValue("recall_at_1", out var v) ? v : double.NaN).Where(v => !double.IsNaN(v)).ToArray();
        var mrrValues = scenarios.Select(s => s.Metrics.TryGetValue("mrr", out var v) ? v : double.NaN).Where(v => !double.IsNaN(v)).ToArray();
        var p95 = operations.Count == 0 ? 0 : operations.Max(o => o.P95Ms);
        return new EvaluationSummary(
            TotalScenarios: total,
            PassedScenarios: passed,
            FailedScenarios: total - passed,
            ScenarioPassRate: total == 0 ? 0 : (double)passed / total,
            OwnerLeakCount: ownerLeaks,
            RecallAt1: recallValues.Length == 0 ? 0 : recallValues.Average(),
            Mrr: mrrValues.Length == 0 ? 0 : mrrValues.Average(),
            P95Ms: p95);
    }

    private static double Percentile(double[] sortedValues, double percentile)
    {
        if (sortedValues.Length == 0) return 0;
        var rank = (int)Math.Ceiling(percentile / 100.0 * sortedValues.Length) - 1;
        return sortedValues[Math.Clamp(rank, 0, sortedValues.Length - 1)];
    }

    private static bool TryParseIterations(string? value, out int iterations)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            iterations = 3;
            return true;
        }

        return int.TryParse(value, out iterations) && iterations is >= 1 and <= 50;
    }

    private static string ResolveOutputPath(string? outputPath, DateTimeOffset generatedAt)
    {
        var path = string.IsNullOrWhiteSpace(outputPath)
            ? Path.Combine("artifacts", "evaluation", $"memory-evaluation-{generatedAt:yyyyMMdd-HHmmss}.json")
            : outputPath;
        return Path.GetFullPath(path);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private float[] UnitVector(int axis)
    {
        var vector = new float[_neo4j.EmbeddingDimensions];
        if (vector.Length == 0) return vector;
        vector[Math.Clamp(axis, 0, vector.Length - 1)] = 1.0f;
        return vector;
    }

    private static Entity NewEntity(string name, string type, string? owner, float[] embedding) => new()
    {
        EntityId = Id("entity"),
        Name = name,
        Type = type,
        Description = $"Evaluation fixture entity for {name}",
        Confidence = 0.95,
        OwnerId = owner,
        Embedding = embedding,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Metadata = new Dictionary<string, object> { ["evaluation"] = true }
    };

    private static Fact NewFact(
        string subject,
        string predicate,
        string obj,
        string? owner,
        float[] embedding,
        IReadOnlyList<string>? sourceMessageIds = null) => new()
    {
        FactId = Id("fact"),
        Subject = subject,
        Predicate = predicate,
        Object = obj,
        Confidence = 0.95,
        OwnerId = owner,
        Embedding = embedding,
        SourceMessageIds = sourceMessageIds ?? Array.Empty<string>(),
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Metadata = new Dictionary<string, object> { ["evaluation"] = true }
    };

    private static Preference NewPreference(string category, string text, string? owner, float[] embedding) => new()
    {
        PreferenceId = Id("preference"),
        Category = category,
        PreferenceText = text,
        Context = "memory evaluation",
        Confidence = 0.95,
        OwnerId = owner,
        Embedding = embedding,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Metadata = new Dictionary<string, object> { ["evaluation"] = true }
    };

    private sealed record OperationSample(string Operation, double DurationMs);

    private sealed record EvaluationReport(
        DateTimeOffset GeneratedAtUtc,
        string RunId,
        string OwnerPrefix,
        int Iterations,
        string Database,
        int EmbeddingDimensions,
        EvaluationSummary Summary,
        IReadOnlyList<EvaluationScenarioResult> Scenarios,
        IReadOnlyList<EvaluationOperationMetric> Operations);

    private sealed record EvaluationSummary(
        int TotalScenarios,
        int PassedScenarios,
        int FailedScenarios,
        double ScenarioPassRate,
        double OwnerLeakCount,
        double RecallAt1,
        double Mrr,
        double P95Ms);

    private sealed record EvaluationScenarioResult(
        string Id,
        string Name,
        int Iteration,
        bool Passed,
        double DurationMs,
        IReadOnlyDictionary<string, double> Metrics,
        string? Error);

    private sealed record EvaluationOperationMetric(
        string Operation,
        int Count,
        double MinMs,
        double P50Ms,
        double P95Ms,
        double P99Ms,
        double MaxMs);
}
