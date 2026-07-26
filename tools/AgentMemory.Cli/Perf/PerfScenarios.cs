using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentMemory.Cli.Perf;

/// <summary>A named, versioned unit of measurement.</summary>
/// <remarks>
/// Ids are stable forever. Adding a scenario is additive; <em>changing</em> one requires a new id, so a
/// number recorded today stays comparable to one recorded a year from now.
/// </remarks>
public sealed record PerfScenario(
    string Id,
    string Description,
    Func<ScenarioContext, Task> RunAsync,
    bool SupportsInterleavedAb = true,
    PerfDependencyLatencyPreset? DependencyLatency = null,
    Func<ScenarioSetupContext, Task>? SetupAsync = null,
    Func<ScenarioVerificationContext, Task>? VerifyAsync = null)
{
    public async Task ExecuteAsync(ScenarioContext context)
    {
        using var latencyScope = DependencyLatency is null
            ? null
            : context.Profile.DependencyLatency.Push(DependencyLatency);
        await RunAsync(context).ConfigureAwait(false);
    }

    public Task PrepareAsync(ScenarioSetupContext context) =>
        SetupAsync?.Invoke(context) ?? Task.CompletedTask;

    public Task ValidateAsync(ScenarioVerificationContext context) =>
        VerifyAsync?.Invoke(context) ?? Task.CompletedTask;
}

public sealed record ScenarioSetupContext(
    HermeticProfile Profile,
    int Iteration,
    string Phase,
    string? Variant,
    CancellationToken CancellationToken);

/// <summary>Everything a scenario body needs for one iteration.</summary>
/// <param name="Phase">
/// <c>warmup</c> or <c>measure</c>. Scenarios that create per-iteration state must include this in the
/// key they derive — both phases count iterations from zero, so the index alone is not unique.
/// </param>
public sealed record ScenarioContext(
    HermeticProfile Profile,
    Neo4jMemoryContextProvider Provider,
    TurnRecord Turn,
    int Iteration,
    string Phase,
    string? Variant,
    RecallOptions RecallOptions,
    CancellationToken CancellationToken);

public sealed record ScenarioVerificationContext(
    HermeticProfile Profile,
    TurnRecord Turn,
    int Iteration,
    string Phase,
    string? Variant,
    CancellationToken CancellationToken);

/// <summary>
/// The versioned performance scenario catalog covers read- and write-path controls at shipped
/// defaults. Together they replace estimates with facts about recall cost before the model runs and
/// ingestion cost after it, including turns that exercise policy and workload extremes.
/// </summary>
public static class PerfScenarios
{
    public static IReadOnlyList<PerfScenario> All { get; } =
    [
        new(
            "PERF-R-01",
            "Greeting-only turn at the shipped default recall policy",
            GreetingRecallAsync),
        new("PERF-R-04", "Full multi-category recall at shipped defaults", RecallAsync),
        new(
            "PERF-R-07",
            "Degraded dependency recall (embedding 2 s, database transaction 250 ms)",
            DegradedRecallAsync,
            DependencyLatency: PerfDependencyLatencyPreset.Degraded),
        new(
            "PERF-R-08",
            "GraphRAG-enabled full recall with deterministic context",
            GraphRagRecallAsync),
        new(
            "PERF-W-02",
            "Single response message, extraction enabled (shipped defaults)",
            StoreAndExtractAsync,
            SupportsInterleavedAb: false,
            SetupAsync: PrepareStoreAndExtractAsync),
        new(
            "PERF-W-03",
            "Six-message tool-heavy response turn, extraction enabled",
            StoreToolHeavyAndExtractAsync,
            SupportsInterleavedAb: false,
            SetupAsync: PrepareToolHeavyAndExtractAsync),
        new(
            "PERF-W-05",
            "Whole-session extraction over 50 pre-seeded messages",
            ExtractWholeSessionAsync,
            SupportsInterleavedAb: false,
            SetupAsync: PrepareWholeSessionAsync,
            VerifyAsync: VerifyWholeSessionAsync),
    ];

    internal const string StoreProbeUserMessage =
        "Alice Martin just moved to the Acme Corporation platform team and prefers concise updates.";

    private const int SessionExtractionMessageCount = 50;

    /// <summary>
    /// Input-keyed model responses required by cost scenarios. Kept separate from judged fixture rules:
    /// an unmatched rule deliberately returns an empty extraction, so omitting this entry turns W-02
    /// into a no-op that its self-assertion rejects.
    /// </summary>
    internal static IReadOnlyList<ScriptedChatClient.Rule> ScriptedRules { get; } =
        [new(StoreProbeUserMessage, ScriptedChatClient.ExtractionPayload)];

    public static IReadOnlyList<PerfScenario> Select(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Equals("all", StringComparison.OrdinalIgnoreCase))
            return All;

        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var selected = All.Where(s => wanted.Contains(s.Id, StringComparer.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0)
            throw new ArgumentException(
                $"no scenario matched '{filter}'. Known: {string.Join(", ", All.Select(s => s.Id))}.");
        return selected;
    }

    /// <summary>
    /// PERF-R-01 — a greeting/acknowledgement turn with no memory-retrieval intent. The shipped default
    /// policy still recalls, so this captures the work that matrix rank 1 must eliminate.
    /// </summary>
    private static async Task GreetingRecallAsync(ScenarioContext ctx)
    {
        var identity = ctx.Variant is null
            ? PerfFixture.DefaultIdentity
            : PerfFixture.ForVariant(ctx.Variant);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "thanks, that's great"),
        };

        var context = await ctx.Provider.BuildContextAsync(
            messages,
            identity.SessionId,
            identity.ConversationId,
            ctx.CancellationToken,
            identity.OwnerId).ConfigureAwait(false);

        RecordContext(ctx.Turn, context);

        // This scenario measures today's shipped policy, not a fixture-configured skipping policy.
        // Ten recent messages are guaranteed by the scale-S fixture and are independent of semantic
        // similarity, so they provide a robust proof that the recall path really ran.
        var recent = ctx.Turn.Counter("items.recent");
        var retrieved = ctx.Turn.Counter("items.retrieved");
        var reads = ctx.Turn.Counter("neo4j.tx.read");
        var embeddings = ctx.Turn.Counter("embed.requests");
        var preferences = ctx.Turn.Counter("items.preferences");
        var accessTracked = ctx.Turn.Counter("access_tracking.items");
        var unexpectedSemantic =
            ctx.Turn.Counter("items.relevant") +
            ctx.Turn.Counter("items.entities") +
            ctx.Turn.Counter("items.facts") +
            ctx.Turn.Counter("items.traces");
        if (recent != 10 || preferences != 1 || unexpectedSemantic != 0 ||
            retrieved != 11 || accessTracked != 1 || reads == 0 || embeddings == 0)
        {
            throw new InvalidOperationException(
                $"PERF-R-01 did not exercise shipped-default recall (items.recent={recent}/10, " +
                $"items.preferences={preferences}/1, items.other_semantic={unexpectedSemantic}/0, " +
                $"items.retrieved={retrieved}/11, access_tracking.items={accessTracked}/1, " +
                $"neo4j.tx.read={reads}, embed.requests={embeddings}). The deterministic fixture's " +
                "current hash-bucket overlap is part of this locked scenario shape. Do not configure " +
                "a skipping policy inside this fixture: rank 1 must change product behavior and update " +
                "this expectation explicitly.");
        }
    }

    /// <summary>
    /// PERF-R-04 — the reference read turn: everything the MAF provider does before the model is called.
    /// </summary>
    private static Task RecallAsync(ScenarioContext ctx) =>
        RunDefaultRecallAsync(ctx, "PERF-R-04");

    /// <summary>
    /// PERF-R-07 — the same full recall under deterministic embedding/database degradation. Current
    /// behavior has no deadline, so it must complete with the full item shape and record both waits.
    /// </summary>
    private static async Task DegradedRecallAsync(ScenarioContext ctx)
    {
        await RunDefaultRecallAsync(ctx, "PERF-R-07").ConfigureAwait(false);

        var preset = PerfDependencyLatencyPreset.Degraded;
        var embeddingCalls = ctx.Turn.Counter("injected.embedding_delay.calls");
        var embeddingMs = ctx.Turn.Counter("injected.embedding_delay.ms");
        var databaseCalls = ctx.Turn.Counter("injected.database_delay.calls");
        var databaseMs = ctx.Turn.Counter("injected.database_delay.ms");
        var accessTracked = ctx.Turn.Counter("access_tracking.items");
        var queries = ctx.Turn.Counter("neo4j.queries");
        var embeddingSpans = ctx.Turn.SpanCounts.TryGetValue(
            "memory.recall.embedding", out var embeddingSpanCount)
            ? embeddingSpanCount
            : 0;
        var transactionSpans = ctx.Turn.SpanCounts.TryGetValue(
            "memory.db.tx", out var transactionSpanCount)
            ? transactionSpanCount
            : 0;

        var expectedEmbeddingMs = checked((long)preset.EmbeddingDelay.TotalMilliseconds);
        const long expectedDatabaseCalls = 7;
        var expectedDatabaseMs = checked(
            expectedDatabaseCalls * (long)preset.DatabaseDelay.TotalMilliseconds);
        if (embeddingCalls != 1 || embeddingMs != expectedEmbeddingMs ||
            databaseCalls != expectedDatabaseCalls || databaseMs != expectedDatabaseMs ||
            embeddingSpans != 1 || transactionSpans != expectedDatabaseCalls ||
            accessTracked != 25 || queries != 9)
        {
            throw new InvalidOperationException(
                $"PERF-R-07 did not record its degraded dependency shape " +
                $"(embedding delay calls/ms={embeddingCalls}/{embeddingMs}, expected " +
                $"1/{expectedEmbeddingMs}; database delay calls/ms={databaseCalls}/{databaseMs}, " +
                $"expected {expectedDatabaseCalls}/{expectedDatabaseMs}; embedding spans=" +
                $"{embeddingSpans}/1; transaction spans={transactionSpans}/{expectedDatabaseCalls}; " +
                $"access_tracking.items={accessTracked}/25; neo4j.queries={queries}/9). The scenario " +
                "would not grade timeouts or graceful degradation reliably.");
        }
    }

    /// <summary>
    /// PERF-R-08 — the full memory recall plus a scenario-only deterministic GraphRAG source. The
    /// Agent Framework provider currently finishes its query embedding before the assembler can start
    /// GraphRAG; rank 17 uses this control to prove those two stages overlap after orchestration moves.
    /// </summary>
    private static async Task GraphRagRecallAsync(ScenarioContext ctx)
    {
        var provider = CreateProvider(ctx.Profile, ctx.RecallOptions, enableGraphRag: true);
        var context = await RunDefaultRecallAsync(ctx, "PERF-R-08", provider).ConfigureAwait(false);

        var graphRagSpans = ctx.Turn.SpanCounts.TryGetValue(
            "memory.recall.graphrag", out var graphRagSpanCount)
            ? graphRagSpanCount
            : 0;
        var graphRagCalls = ctx.Turn.Counter("graphrag.calls");
        var graphRagItems = ctx.Turn.Counter("items.graphrag");
        var graphRagDelayCalls = ctx.Turn.Counter("injected.graphrag_delay.calls");
        var graphRagDelayMs = ctx.Turn.Counter("injected.graphrag_delay.ms");
        var embeddings = ctx.Turn.Counter("embed.requests");
        var reads = ctx.Turn.Counter("neo4j.tx.read");
        var writes = ctx.Turn.Counter("neo4j.tx.write");
        var queries = ctx.Turn.Counter("neo4j.queries");
        var accessTracked = ctx.Turn.Counter("access_tracking.items");
        var materialized = context.Messages?.Any(message =>
            message.Text?.Contains(
                DeterministicGraphRagContextSource.FirstMarker,
                StringComparison.Ordinal) == true &&
            message.Text.Contains(
                DeterministicGraphRagContextSource.SecondMarker,
                StringComparison.Ordinal)) == true;

        if (graphRagSpans != 1 || graphRagCalls != 1 || graphRagItems != 2 ||
            graphRagDelayCalls != 1 ||
            graphRagDelayMs != DeterministicGraphRagContextSource.DelayMilliseconds ||
            embeddings != 1 || reads != 6 || writes != 1 || queries != 9 ||
            accessTracked != 25 || !materialized)
        {
            throw new InvalidOperationException(
                $"PERF-R-08 did not exercise its GraphRAG contract " +
                $"(spans={graphRagSpans}/1, calls={graphRagCalls}/1, items={graphRagItems}/2, " +
                $"delay calls/ms={graphRagDelayCalls}/{graphRagDelayMs}, expected " +
                $"1/{DeterministicGraphRagContextSource.DelayMilliseconds}; embed.requests=" +
                $"{embeddings}/1; neo4j read/write/queries={reads}/{writes}/{queries}, expected " +
                $"6/1/9; access_tracking.items={accessTracked}/25; materialized={materialized}/true). " +
                "A disabled or unregistered GraphRAG source would make this measurement a no-op.");
        }
    }

    private static async Task<AIContext> RunDefaultRecallAsync(
        ScenarioContext ctx,
        string scenarioId,
        Neo4jMemoryContextProvider? provider = null)
    {
        var identity = ctx.Variant is null
            ? PerfFixture.DefaultIdentity
            : PerfFixture.ForVariant(ctx.Variant);
        var messages = new[] { new ChatMessage(ChatRole.User, PerfFixture.ProbeQueryFor(identity)) };

        var context = await (provider ?? ctx.Provider).BuildContextAsync(
            messages,
            identity.SessionId,
            identity.ConversationId,
            ctx.CancellationToken,
            identity.OwnerId).ConfigureAwait(false);

        RecordContext(ctx.Turn, context);

        // Self-check, not decoration. A fixture whose vectors drift below MinSimilarityScore produces an
        // empty recall that still "succeeds" — and a baseline recorded from that would understate the
        // real cost by an order of magnitude and be quietly wrong forever after.
        var expected = PerfFixture.ExpectedRecall(ctx.RecallOptions);
        var retrieved = ctx.Turn.Counter("items.retrieved");
        if (retrieved != expected.Total)
        {
            var breakdown = string.Join(", ", expected.ByCategory
                .Select(kv => $"{kv.Key}={ctx.Turn.Counter($"items.{kv.Key}")}/{kv.Value}"));
            throw new InvalidOperationException(
                $"{scenarioId} recalled {retrieved} items but expected {expected.Total} " +
                $"({breakdown}). The fixture is not exercising the configured recall shape, so this " +
                "measurement would be misleading. Check MinSimilarityScore and the seeded embeddings.");
        }

        return context;
    }

    private static void RecordContext(TurnRecord turn, AIContext context)
    {
        // Materialized once: AIContext.Messages is an enumerable, so counting and summing it separately
        // would enumerate it twice.
        var contextMessages = context.Messages?.ToList() ?? [];
        turn.Add("context.messages", contextMessages.Count);
        turn.Add("context.chars", contextMessages.Sum(m => (long)(m.Text?.Length ?? 0)));
    }

    /// <summary>
    /// PERF-W-02 — the reference write turn: response-message persistence plus automatic extraction,
    /// which is what <c>AutoExtractOnPersist=true</c> makes every turn pay today.
    /// </summary>
    private static async Task StoreAndExtractAsync(ScenarioContext ctx)
    {
        // A distinct session plus the unmeasured reset keeps iterations independent. The owner remains
        // the seeded fixture owner because entity resolution against that fixed graph is part of this
        // shipped-default workload; using a fresh, empty owner changes queries and embedding requests.
        //
        // The PHASE must be part of the key, not just the index. Warm-up and measurement both count from
        // zero, so keying on the index alone made measured iteration 0 run against the session warm-up
        // iteration 0 had already populated — the one measured turn that was not a clean turn.
        var sessionId = $"perf-w02-{ctx.Phase}-{ctx.Iteration}";
        var conversationId = $"{sessionId}-conv";
        var requestMessages = new[]
        {
            new ChatMessage(ChatRole.User, StoreProbeUserMessage),
        };
        var responseMessages = new[]
        {
            new ChatMessage(ChatRole.Assistant,
                "Noted — Alice Martin is on the Acme Corporation platform team and prefers concise written updates."),
        };

        await ctx.Provider.PerformStoreAsync(
            requestMessages,
            responseMessages,
            sessionId,
            conversationId,
            ctx.CancellationToken,
            PerfFixture.OwnerId).ConfigureAwait(false);

        AssertScriptedExtraction(ctx, "PERF-W-02");
    }

    /// <summary>
    /// PERF-W-03 — a tool-heavy turn with six non-empty response messages. The current provider
    /// persists each response separately, so this captures the fan-out that matrix rank 9 must batch.
    /// </summary>
    private static async Task StoreToolHeavyAndExtractAsync(ScenarioContext ctx)
    {
        var sessionId = $"perf-w03-{ctx.Phase}-{ctx.Iteration}";
        var conversationId = $"{sessionId}-conv";
        var requestMessages = new[]
        {
            new ChatMessage(ChatRole.User, StoreProbeUserMessage),
        };
        var responseMessages = new[]
        {
            new ChatMessage(ChatRole.Assistant, "I'll check the account details."),
            new ChatMessage(ChatRole.Tool, "Account lookup completed for Acme Corporation."),
            new ChatMessage(ChatRole.Assistant, "I'll inspect the platform deployment."),
            new ChatMessage(ChatRole.Tool, "Deployment lookup completed: all services are healthy."),
            new ChatMessage(ChatRole.Assistant, "I'll verify the notification settings."),
            new ChatMessage(ChatRole.Tool, "Notification lookup completed: concise written updates are preferred."),
        };

        await ctx.Provider.PerformStoreAsync(
            requestMessages,
            responseMessages,
            sessionId,
            conversationId,
            ctx.CancellationToken,
            PerfFixture.OwnerId).ConfigureAwait(false);

        var storedMessages = ctx.Turn.Counter("store.messages");
        if (storedMessages != responseMessages.Length)
        {
            throw new InvalidOperationException(
                $"PERF-W-03 stored {storedMessages} response messages but expected " +
                $"{responseMessages.Length}. The scenario would not exercise multi-message " +
                "persistence, so it cannot grade rank 9. Check that every fixture response has " +
                "non-empty text and reaches StoreResponseMessagesAsync.");
        }

        AssertScriptedExtraction(ctx, "PERF-W-03");
    }

    private static async Task PrepareStoreAndExtractAsync(ScenarioSetupContext ctx)
    {
        var responseMessages = new[]
        {
            new ChatMessage(ChatRole.Assistant,
                "Noted — Alice Martin is on the Acme Corporation platform team and prefers concise written updates."),
        };
        await ResetAndPrimeWriteScenarioAsync(ctx, "perf-w02-prime", responseMessages).ConfigureAwait(false);
    }

    private static async Task PrepareToolHeavyAndExtractAsync(ScenarioSetupContext ctx)
    {
        var responseMessages = new[]
        {
            new ChatMessage(ChatRole.Assistant, "I'll check the account details."),
            new ChatMessage(ChatRole.Tool, "Account lookup completed for Acme Corporation."),
            new ChatMessage(ChatRole.Assistant, "I'll inspect the platform deployment."),
            new ChatMessage(ChatRole.Tool, "Deployment lookup completed: all services are healthy."),
            new ChatMessage(ChatRole.Assistant, "I'll verify the notification settings."),
            new ChatMessage(ChatRole.Tool,
                "Notification lookup completed: concise written updates are preferred."),
        };
        await ResetAndPrimeWriteScenarioAsync(ctx, "perf-w03-prime", responseMessages).ConfigureAwait(false);
    }

    /// <summary>
    /// Establishes the same one-prior-turn state before every iteration. The old fixture obtained this
    /// state accidentally from its warm-up and then kept accumulating into it; explicit priming preserves
    /// the shipped-default resolution path while making materialized payload bytes exact and repeatable.
    /// </summary>
    private static async Task ResetAndPrimeWriteScenarioAsync(
        ScenarioSetupContext ctx,
        string sessionId,
        IReadOnlyList<ChatMessage> responseMessages)
    {
        await ResetWriteScenarioAsync(ctx).ConfigureAwait(false);

        var provider = CreateProvider(ctx.Profile);
        var requestMessages = new[]
        {
            new ChatMessage(ChatRole.User, StoreProbeUserMessage),
        };
        await provider.PerformStoreAsync(
            requestMessages,
            responseMessages,
            sessionId,
            $"{sessionId}-conv",
            ctx.CancellationToken,
            PerfFixture.OwnerId).ConfigureAwait(false);
    }

    /// <summary>
    /// Restores the write scenarios to the same graph state before every warm-up and measured turn.
    /// Setup uses the raw driver and runs before <see cref="PerfCollector.BeginTurn"/>, so fixture
    /// maintenance cannot inflate product counters.
    /// </summary>
    private static async Task ResetWriteScenarioAsync(ScenarioSetupContext ctx)
    {
        const string cypher = """
            MATCH (n)
            WHERE (
                    n.owner_id = $ownerId
                    AND (
                        (n:Entity AND NOT n.id STARTS WITH 'perf-entity-')
                        OR (n:Fact AND NOT n.id STARTS WITH 'perf-fact-')
                        OR (n:Preference AND NOT n.id STARTS WITH 'perf-pref-')
                    )
                  )
               OR (
                    (n:Message OR n:Conversation)
                    AND (
                        n.session_id STARTS WITH 'perf-w02-'
                        OR n.session_id = 'perf-w02-prime'
                        OR n.session_id STARTS WITH 'perf-w03-'
                        OR n.session_id = 'perf-w03-prime'
                    )
                  )
            DETACH DELETE n
            """;

        await using var session = ctx.Profile.Driver.AsyncSession();
        await session.ExecuteWriteAsync(async transaction =>
        {
            var cursor = await transaction.RunAsync(cypher, new { ownerId = PerfFixture.OwnerId })
                .ConfigureAwait(false);
            await cursor.ConsumeAsync().ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// PERF-W-05 — reads one complete 50-message session and extracts once. Together with the existing
    /// per-turn control, this is the baseline rank 8 needs to grade deferred/windowed extraction.
    /// </summary>
    private static async Task ExtractWholeSessionAsync(ScenarioContext ctx)
    {
        var sessionId = SessionExtractionSessionId(ctx.Phase, ctx.Iteration);
        var ownerId = SessionExtractionOwnerId(ctx.Phase, ctx.Iteration);
        var memory = ctx.Profile.Services.GetRequiredService<IMemoryService>();

        await memory.ExtractFromSessionAsync(sessionId, ownerId, ctx.CancellationToken).ConfigureAwait(false);

        var sourceMessages = ctx.Turn.Counter("extract.source_messages");
        var modelCalls = ctx.Turn.Counter("llm.calls");
        var entities = ctx.Turn.Counter("persist.entities");
        var facts = ctx.Turn.Counter("persist.facts");
        var preferences = ctx.Turn.Counter("persist.preferences");
        if (sourceMessages != SessionExtractionMessageCount || modelCalls != 4 ||
            entities != 2 || facts != 2 || preferences != 1)
        {
            throw new InvalidOperationException(
                $"PERF-W-05 did not exercise whole-session extraction " +
                $"(extract.source_messages={sourceMessages}/{SessionExtractionMessageCount}, " +
                $"llm.calls={modelCalls}/4, persist entities/facts/preferences=" +
                $"{entities}/{facts}/{preferences}, expected 2/2/1). A capped or empty session, " +
                "missing extractor, or empty scripted response would make rank 8's baseline invalid.");
        }
    }

    private static Task PrepareWholeSessionAsync(ScenarioSetupContext ctx)
    {
        var sessionId = SessionExtractionSessionId(ctx.Phase, ctx.Iteration);
        return PerfFixture.SeedSessionExtractionAsync(
            ctx.Profile,
            sessionId,
            $"{sessionId}-conv",
            SessionExtractionMessageCount,
            ctx.CancellationToken);
    }

    private static async Task VerifyWholeSessionAsync(ScenarioVerificationContext ctx)
    {
        var sessionId = SessionExtractionSessionId(ctx.Phase, ctx.Iteration);
        var shape = await PerfFixture.InspectSessionExtractionAsync(
            ctx.Profile,
            sessionId,
            SessionExtractionOwnerId(ctx.Phase, ctx.Iteration)).ConfigureAwait(false);

        const int expectedMemories = 5;
        var expectedProvenance = expectedMemories * SessionExtractionMessageCount;
        if (shape.Messages != SessionExtractionMessageCount ||
            shape.Entities != 2 || shape.Facts != 2 || shape.Preferences != 1 ||
            shape.ProvenanceRelationships != expectedProvenance)
        {
            throw new InvalidOperationException(
                $"PERF-W-05 graph read-back failed (messages={shape.Messages}/" +
                $"{SessionExtractionMessageCount}, entities/facts/preferences=" +
                $"{shape.Entities}/{shape.Facts}/{shape.Preferences}, expected 2/2/1; " +
                $"provenance={shape.ProvenanceRelationships}/{expectedProvenance}). Counters alone " +
                "cannot prove that the extracted memories were actually learned.");
        }
    }

    private static string SessionExtractionSessionId(string phase, int iteration) =>
        $"perf-w05-{phase}-{iteration}";

    private static string SessionExtractionOwnerId(string phase, int iteration) =>
        $"{SessionExtractionSessionId(phase, iteration)}-owner";

    private static void AssertScriptedExtraction(ScenarioContext ctx, string scenarioId)
    {
        // The mirror of the recall self-check: a scripted model returning unparseable output would make
        // extraction yield nothing, persistence write nothing, and this scenario measure a no-op. Model
        // calls alone are not evidence: an empty-but-valid response still records all four calls.
        var modelCalls = ctx.Turn.Counter("llm.calls");
        var entities = ctx.Turn.Counter("persist.entities");
        var facts = ctx.Turn.Counter("persist.facts");
        var preferences = ctx.Turn.Counter("persist.preferences");
        if (modelCalls == 0 || entities == 0 || facts == 0 || preferences == 0)
        {
            throw new InvalidOperationException(
                $"{scenarioId} did not persist the scripted extraction (llm.calls={modelCalls}, " +
                $"persist.entities={entities}, persist.facts={facts}, " +
                $"persist.preferences={preferences}). The scenario would measure a no-op. Check that " +
                "LLM extraction is opted in, AutoExtractOnPersist is true, and the scripted client " +
                "returned its cost-scenario payload.");
        }
    }

    /// <summary>
    /// Builds the MAF context provider used by both scenarios, from the same services a host would get.
    /// </summary>
    /// <remarks>
    /// Constructed directly rather than resolved: the AgentFramework options types are not registered by
    /// <c>AddNeo4jAgentMemory</c>, and passing explicit defaults here documents that the scenarios run
    /// against shipped defaults rather than a tuned configuration.
    /// </remarks>
    public static Neo4jMemoryContextProvider CreateProvider(
        HermeticProfile profile,
        RecallOptions? recallOptions = null,
        bool enableGraphRag = false)
    {
        var services = profile.Services;
        var configuredMemory = services.GetRequiredService<IOptions<MemoryOptions>>().Value;
        var selectedRecall = recallOptions ?? configuredMemory.Recall;
        var selectedMemory = configuredMemory with
        {
            Recall = selectedRecall,
            EnableGraphRag = enableGraphRag || configuredMemory.EnableGraphRag,
        };
        var memoryService = enableGraphRag
            ? CreateGraphRagMemoryService(services, selectedMemory)
            : services.GetRequiredService<IMemoryService>();
        return new Neo4jMemoryContextProvider(
            memoryService,
            services.GetRequiredService<IEmbeddingOrchestrator>(),
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IIdGenerator>(),
            Options.Create(selectedMemory),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            services.GetRequiredService<ILogger<Neo4jMemoryContextProvider>>());
    }

    private static IMemoryService CreateGraphRagMemoryService(
        IServiceProvider services,
        MemoryOptions memoryOptions)
    {
        var options = Options.Create(memoryOptions);
        var shortTerm = services.GetRequiredService<IShortTermMemoryService>();
        var embedding = services.GetRequiredService<IEmbeddingOrchestrator>();
        var assembler = new MemoryContextAssembler(
            shortTerm,
            services.GetRequiredService<ILongTermMemoryService>(),
            services.GetRequiredService<IReasoningMemoryService>(),
            services.GetRequiredService<IGraphRagContextSource>(),
            embedding,
            services.GetRequiredService<IClock>(),
            options,
            services.GetRequiredService<ILogger<MemoryContextAssembler>>(),
            services.GetRequiredService<IMemoryIsolationPolicy>(),
            services.GetService<IWritableMemoryRankingContext>());

        return new MemoryService(
            shortTerm,
            assembler,
            services.GetRequiredService<IMemoryExtractionPipeline>(),
            services.GetRequiredService<IEntityRepository>(),
            services.GetRequiredService<IFactRepository>(),
            services.GetRequiredService<IPreferenceRepository>(),
            embedding,
            options,
            services.GetRequiredService<IClock>(),
            services.GetRequiredService<IIdGenerator>(),
            services.GetRequiredService<ILogger<MemoryService>>(),
            services.GetService<IMemoryDecayService>(),
            services.GetService<IConversationRepository>());
    }
}
