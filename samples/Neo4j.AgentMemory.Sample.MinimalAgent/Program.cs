// =============================================================================
// Neo4j Agent Memory — Minimal MAF Sample
//
// Prerequisites:
//   • Neo4j 5.11+ (optional for demo mode — graceful fallback if unavailable)
//   • .NET 9 SDK
//
// Configure the connection via appsettings.json or environment variables:
//   Neo4j__Uri      (default: bolt://localhost:7687)
//   Neo4j__Username (default: neo4j)
//   Neo4j__Password (required for a real run)
// =============================================================================

using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using Neo4j.AgentMemory.AgentFramework.Tools;
using Neo4j.AgentMemory.Core;
using Neo4j.AgentMemory.Core.Stubs;
using Neo4j.AgentMemory.Neo4j.Infrastructure;

// ── 0. Build host ─────────────────────────────────────────────────────────────
var builder = Host.CreateApplicationBuilder(args);

// ── 1. Neo4j infrastructure ───────────────────────────────────────────────────
// Reads Uri / Username / Password from appsettings.json or environment variables.
builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri      = builder.Configuration["Neo4j:Uri"]      ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
});

// ── 2. Core memory services ───────────────────────────────────────────────────
builder.Services.AddAgentMemoryCore(_ =>
{
    // MemoryOptions is a record; all sub-options default to sensible values.
    // See MemoryOptions, ShortTermMemoryOptions, LongTermMemoryOptions, etc.
    // for the full configuration surface.
});

// Provide default IClock and IIdGenerator implementations.
// Replace with custom implementations for testing or special time/ID requirements.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();

// StubEmbeddingGenerator returns deterministic random vectors and is suitable only
// for compilation and structure tests. Replace with a real IEmbeddingGenerator<string, Embedding<float>>
// such as OpenAI text-embedding-3-small before using semantic search or LLM extraction.
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();

// ── 3. MAF adapter ────────────────────────────────────────────────────────────
builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist           = true;
    options.ContextFormat.IncludeEntities  = true;
    options.ContextFormat.IncludeFacts     = true;
    options.ContextFormat.IncludePreferences = true;
});

// AgentTraceRecorder and MemoryToolFactory are not auto-registered by
// AddAgentMemoryFramework — add them explicitly when needed.
builder.Services.AddScoped<AgentTraceRecorder>();
builder.Services.AddScoped<MemoryToolFactory>();

var host = builder.Build();

// ── Demo run ──────────────────────────────────────────────────────────────────
await RunDemoAsync(host.Services);

// =============================================================================
// Demo: shows the four integration points a MAF agent would use.
// =============================================================================
static async Task RunDemoAsync(IServiceProvider rootServices)
{
    var logger = rootServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== Neo4j Agent Memory — Minimal MAF Sample ===");

    // Create a scoped lifetime to match a typical per-request / per-run agent scope.
    await using var scope = rootServices.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    const string sessionId      = "demo-session-01";
    const string conversationId = "demo-conv-01";
    var facade = sp.GetRequiredService<Neo4jMicrosoftMemoryFacade>();

    // ── Step 1: Pre-run — retrieve prior context ───────────────────────────────
    // In a real MAF agent pipeline, call this before invoking the agent to seed
    // the system prompt with relevant history, entities, facts, and preferences.
    // Without a live Neo4j instance the call falls back to an empty list.
    logger.LogInformation("[1] Fetching prior context for session '{SessionId}'…", sessionId);
    var priorMessages = await facade.GetContextForRunAsync(
        messages: [],
        sessionId: sessionId,
        conversationId: conversationId);
    logger.LogInformation("    Retrieved {Count} prior message(s).", priorMessages.Count);

    // ── Step 2: Simulated agent turn ───────────────────────────────────────────
    // Replace this block with your actual Microsoft.Agents / MAF agent invocation.
    // The agent receives `priorMessages` as additional context alongside the
    // user's current input.
    var newMessages = new List<ChatMessage>
    {
        new(ChatRole.User,      "My name is Alice and I prefer dark mode."),
        new(ChatRole.Assistant, "Got it, Alice! I've noted your dark-mode preference."),
    };
    logger.LogInformation("[2] Agent produced {Count} new message(s).", newMessages.Count);

    // ── Step 3: Post-run — persist messages ────────────────────────────────────
    // Stores all new messages in Neo4j short-term memory.
    // When AutoExtractOnPersist = true, the extraction pipeline also runs to
    // surface entities, facts, and preferences into long-term memory.
    logger.LogInformation("[3] Persisting messages to Neo4j memory…");
    await facade.PersistAfterRunAsync(newMessages, sessionId, conversationId);
    logger.LogInformation("    Messages persisted.");

    // ── Step 4: Memory tools ────────────────────────────────────────────────────
    // MemoryToolFactory.CreateTools() returns the six standard memory tools that
    // can be registered with any MAF / function-calling-capable agent.
    var toolFactory = sp.GetRequiredService<MemoryToolFactory>();
    var tools       = toolFactory.CreateTools();
    logger.LogInformation("[4] Available memory tools ({Count}):", tools.Count);
    foreach (var tool in tools)
        logger.LogInformation("    • {Name} — {Description}", tool.Name, tool.Description);

    // ── Step 5: Reasoning trace ─────────────────────────────────────────────────
    // AgentTraceRecorder captures agent reasoning steps (thought / action /
    // observation) and tool calls as persistent reasoning traces in Neo4j.
    logger.LogInformation("[5] Recording a reasoning trace…");
    var traceRecorder = sp.GetRequiredService<AgentTraceRecorder>();
    try
    {
        var trace = await traceRecorder.StartTraceAsync(
            task: "Answer user question about dark-mode preference",
            sessionId: sessionId);

        await traceRecorder.RecordStepAsync(
            traceId:  trace.TraceId,
            stepType: "thought",
            content:  "User mentioned a UI preference — recording it in long-term memory.");

        await traceRecorder.CompleteTraceAsync(
            traceId: trace.TraceId,
            outcome: "Preference noted.");

        logger.LogInformation("    Trace recorded successfully.");
    }
    catch (Exception ex)
    {
        // Expected when no live Neo4j instance is available.
        logger.LogWarning("    Trace recording skipped (no live Neo4j): {Message}", ex.Message);
    }

    logger.LogInformation("=== Demo complete. ===");
}
