// =============================================================================
// Neo4j Agent Memory — Blended Memory + GraphRAG Sample
//
// Demonstrates combining persistent agent memory with GraphRAG retrieval,
// plus OpenTelemetry observability for all memory operations.
//
// Prerequisites:
//   • Neo4j 5.11+ with vector index "knowledge_vectors" (optional — graceful
//     fallback if unavailable)
//   • .NET 9 SDK
//
// Configure via appsettings.json or environment variables:
//   Neo4j__Uri           (default: bolt://localhost:7687)
//   Neo4j__Username      (default: neo4j)
//   Neo4j__Password      (required for a real run)
//   Neo4j__GraphRag__IndexName (default: knowledge_vectors)
// =============================================================================

using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using Neo4j.AgentMemory.AgentFramework.Tools;
using Neo4j.AgentMemory.Core;
using Neo4j.AgentMemory.Core.Stubs;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Observability;

// ── 0. Build host ─────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// ── 1. Neo4j infrastructure ───────────────────────────────────────────────────
builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri      = builder.Configuration["Neo4j:Uri"]      ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
});

// ── 2. Core memory services with GraphRAG enabled ─────────────────────────────
builder.Services.AddAgentMemoryCore(options =>
{
    options = options with
    {
        EnableGraphRag = true,
        Recall = new RecallOptions
        {
            BlendMode       = RetrievalBlendMode.Blended,
            MaxGraphRagItems = 5,
            MaxEntities     = 10,
            MaxFacts        = 10,
        }
    };
});

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();

// StubEmbeddingGenerator is for compilation and structure validation only.
// Replace with a real IEmbeddingGenerator<string, Embedding<float>> (e.g. OpenAI) for semantic search.
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();

// IEmbeddingGenerator<string, Embedding<float>> is required by the GraphRAG adapter
// to embed queries for vector search. With unified embedding, both Core and GraphRAG
// use the same IEmbeddingGenerator registration.
// builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();  // already registered above

// ── 3. GraphRAG adapter ───────────────────────────────────────────────────────
// Connects agent memory retrieval to a Neo4j vector + fulltext index.
// Call this BEFORE AddAgentMemoryObservability so the decorator wraps this registration.
var graphRagIndexName = builder.Configuration["Neo4j:GraphRag:IndexName"] ?? "knowledge_vectors";
builder.Services.AddGraphRagAdapter(options =>
{
    options.IndexName         = graphRagIndexName;
    options.FulltextIndexName = builder.Configuration["Neo4j:GraphRag:FulltextIndexName"] ?? $"{graphRagIndexName}_fulltext";
    options.SearchMode        = GraphRagSearchMode.Hybrid;
    options.TopK              = 5;
    options.FilterStopWords   = true;
});

// ── 4. MAF adapter ────────────────────────────────────────────────────────────
builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist             = true;
    options.ContextFormat.IncludeEntities   = true;
    options.ContextFormat.IncludeFacts      = true;
    options.ContextFormat.IncludePreferences = true;
});

builder.Services.AddScoped<AgentTraceRecorder>();
builder.Services.AddScoped<MemoryToolFactory>();

// ── 5. Observability (must be LAST — decorates previously registered services) ─
// Wraps IMemoryService and IGraphRagContextSource with OTel tracing + metrics.
// Add OpenTelemetry SDK exporters (OTLP, console, etc.) here in production.
builder.Services.AddAgentMemoryObservability();

var host = builder.Build();

// ── Observability: wire up a console ActivityListener for demo purposes ────────
// In production, configure a real OpenTelemetry exporter (e.g. AddOtlpExporter).
using var listener = new ActivityListener
{
    ShouldListenTo  = source => source.Name == MemoryActivitySource.Name,
    Sample          = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
    ActivityStarted = activity => Console.WriteLine($"  [OTel] ▶ {activity.OperationName}"),
    ActivityStopped = activity => Console.WriteLine($"  [OTel] ■ {activity.OperationName} ({activity.Duration.TotalMilliseconds:F1} ms)"),
};
ActivitySource.AddActivityListener(listener);

// ── Demo run ──────────────────────────────────────────────────────────────────
await RunDemoAsync(host.Services);

// =============================================================================
// Demo: retrieval modes — MemoryOnly, GraphRagOnly, Blended
// =============================================================================
static async Task RunDemoAsync(IServiceProvider rootServices)
{
    var logger = rootServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== Neo4j Agent Memory — Blended Memory + GraphRAG Sample ===");

    await using var scope = rootServices.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    const string sessionId      = "blended-session-01";
    const string conversationId = "blended-conv-01";
    const string userQuery      = "What are the main facts about Alice and her preferences?";

    // ── Mode A: Memory-only retrieval ──────────────────────────────────────────
    logger.LogInformation("[A] Memory-only retrieval…");
    await DemoRecallModeAsync(sp, sessionId, userQuery, RetrievalBlendMode.MemoryOnly, logger);

    // ── Mode B: GraphRAG-only retrieval ───────────────────────────────────────
    logger.LogInformation("[B] GraphRAG-only retrieval…");
    await DemoGraphRagOnlyAsync(sp, sessionId, userQuery, logger);

    // ── Mode C: Blended retrieval via Facade ──────────────────────────────────
    logger.LogInformation("[C] Blended pre-run context assembly…");
    var facade = sp.GetRequiredService<Neo4jMicrosoftMemoryFacade>();
    var priorMessages = await facade.GetContextForRunAsync(
        messages: [],
        sessionId: sessionId,
        conversationId: conversationId);
    logger.LogInformation("    Retrieved {Count} prior message(s) in blended mode.", priorMessages.Count);

    // ── Step 1: Simulated agent turn ───────────────────────────────────────────
    var newMessages = new List<Microsoft.Extensions.AI.ChatMessage>
    {
        new(ChatRole.User,      "My name is Alice and I prefer dark mode."),
        new(ChatRole.Assistant, "Got it! I'll remember your dark-mode preference, Alice."),
    };
    logger.LogInformation("[1] Agent produced {Count} new message(s).", newMessages.Count);

    // ── Step 2: Post-run persistence ───────────────────────────────────────────
    logger.LogInformation("[2] Persisting messages to Neo4j memory…");
    await facade.PersistAfterRunAsync(newMessages, sessionId, conversationId);
    logger.LogInformation("    Messages persisted.");

    // ── Step 3: Memory tools — MAF-compatible AIFunction instances ──────────────
    var toolFactory = sp.GetRequiredService<MemoryToolFactory>();
    var aiFunctions = toolFactory.CreateAIFunctions();
    logger.LogInformation("[3] Available memory AIFunctions ({Count}):", aiFunctions.Count);
    foreach (var fn in aiFunctions)
        logger.LogInformation("    • {Name} — {Description}", fn.Name, fn.Description);

    // ── Step 4: Reasoning trace ─────────────────────────────────────────────────
    logger.LogInformation("[4] Recording a reasoning trace…");
    var traceRecorder = sp.GetRequiredService<AgentTraceRecorder>();
    try
    {
        var trace = await traceRecorder.StartTraceAsync(
            task: "Answer user question using blended memory + GraphRAG context",
            sessionId: sessionId);

        await traceRecorder.RecordStepAsync(
            traceId:  trace.TraceId,
            stepType: "thought",
            content:  "Fetching blended context: persistent memory + GraphRAG knowledge graph.");

        await traceRecorder.RecordStepAsync(
            traceId:  trace.TraceId,
            stepType: "observation",
            content:  "Context assembled; entity Alice found in memory; dark-mode preference retrieved.");

        await traceRecorder.CompleteTraceAsync(
            traceId: trace.TraceId,
            outcome: "Blended answer delivered.");

        logger.LogInformation("    Trace recorded successfully.");
    }
    catch (Exception ex)
    {
        logger.LogWarning("    Trace recording skipped (no live Neo4j): {Message}", ex.Message);
    }

    logger.LogInformation("=== Demo complete. ===");
}

// Shows a direct IMemoryService recall with a specific blend mode override.
static async Task DemoRecallModeAsync(
    IServiceProvider sp,
    string sessionId,
    string query,
    RetrievalBlendMode blendMode,
    ILogger logger)
{
    var memoryService = sp.GetRequiredService<IMemoryService>();
    try
    {
        var result = await memoryService.RecallAsync(new RecallRequest
        {
            SessionId = sessionId,
            Query     = query,
            Options   = new RecallOptions { BlendMode = blendMode },
        });
        logger.LogInformation("    Recall ({BlendMode}): {Count} item(s) retrieved.",
            blendMode, result.TotalItemsRetrieved);
    }
    catch (Exception ex)
    {
        logger.LogWarning("    Recall skipped (no live Neo4j): {Message}", ex.Message);
    }
}

// Shows a direct IGraphRagContextSource query to demonstrate GraphRAG-only mode.
static async Task DemoGraphRagOnlyAsync(
    IServiceProvider sp,
    string sessionId,
    string query,
    ILogger logger)
{
    var graphRag = sp.GetRequiredService<IGraphRagContextSource>();
    try
    {
        var result = await graphRag.GetContextAsync(new GraphRagContextRequest
        {
            SessionId = sessionId,
            Query     = query,
            TopK      = 5,
            SearchMode = GraphRagSearchMode.Hybrid,
        });
        logger.LogInformation("    GraphRAG: {Count} context item(s) retrieved.", result.Items.Count);
        foreach (var item in result.Items)
            logger.LogInformation("      score={Score:F3} — {Text}", item.Score, item.Text);
    }
    catch (Exception ex)
    {
        logger.LogWarning("    GraphRAG skipped (no live Neo4j): {Message}", ex.Message);
    }
}

// =============================================================================
// End of sample
// =============================================================================
