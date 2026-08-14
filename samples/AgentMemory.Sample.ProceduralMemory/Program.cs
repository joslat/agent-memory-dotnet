// =============================================================================
// Neo4j Agent Memory — Procedural Memory (closed loop)
//
// The full cycle, end to end: an agent records HOW it completed a task, that trace
// is promoted to a procedure, and a later run recalls it and follows it.
//
// Every other sample demonstrates memory of FACTS — what is true. This one
// demonstrates memory of METHOD — how something was done. They are different
// tiers with different APIs, and until now the second had no runnable example:
// AgentTraceRecorder had seven references inside the library and none in a
// sample, so the closed loop was only ever exercised by the benchmark harness.
//
// Prerequisites:
//   • Neo4j 5.11+ (required — this sample reads back what it writes)
//   • .NET 9 SDK
//
// Requires a real Azure OpenAI embedding model — trace recall is a VECTOR search
// over the task text, so a procedure stored without an embedding is invisible:
//   AZURE_OPENAI_ENDPOINT               (required)
//   AZURE_OPENAI_API_KEY                (required)
//   AZURE_OPENAI_EMBEDDING_DEPLOYMENT   (default: text-embedding-ada-002)
//
// Connection via appsettings.json or environment variables:
//   Neo4j__Uri      (default: bolt://localhost:7687)
//   Neo4j__Username (default: neo4j)
//   Neo4j__Password (required for a real run)
// =============================================================================

using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Samples.Shared;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

if (!RealAzureOpenAI.TryCreate(out var azureClient, out _, out var embeddingDeployment))
{
    RealAzureOpenAI.PrintMissingCredentials("Neo4j Agent Memory — Procedural Memory");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri = builder.Configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
});

builder.Services.AddAgentMemoryCore(_ => { });
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    azureClient.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator());

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host;

await RunAsync(host.Services);

// =============================================================================
// The loop: record → promote → recall → reuse.
// =============================================================================
static async Task RunAsync(IServiceProvider rootServices)
{
    var logger = rootServices.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== Procedural memory: record, promote, recall, reuse ===");

    await using var scope = rootServices.CreateAsyncScope();
    var reasoning = scope.ServiceProvider.GetRequiredService<IReasoningMemoryService>();
    var embeddings = scope.ServiceProvider.GetRequiredService<IEmbeddingOrchestrator>();

    const string task = "Book the 14:05 rail connection for a traveller with a loyalty tier.";
    var owner = MemoryScope.For($"procedural-sample-{Guid.NewGuid():N}");

    // ── Run 1: the agent works the task out the hard way ──────────────────────
    // The ordering below is the kind of thing an agent can only learn by failing:
    // booking needs a hold, a hold needs the traveller's tier, and the tier sits
    // behind a lookup nothing in the prompt mentions.
    logger.LogInformation("[1] First run — discovering the tool ordering by trial.");

    var trace = await reasoning.StartTraceAsync(
        sessionId: "procedural-sample-run-1",
        task: task,
        // Trace recall is a vector search over the TASK. A trace stored without this embedding is
        // written, queryable by id, and invisible to every recall path — which is precisely how
        // procedural memory can look "implemented" while returning nothing.
        taskEmbedding: await embeddings.EmbedAsync(task),
        ownerId: owner.OwnerId);

    foreach (var (step, index) in new[]
             {
                 "LookUpTraveller — resolve the loyalty tier",
                 "CheckServiceBulletin — confirm the 14:05 is running",
                 "PlaceHold — reserve against the tier",
                 "Book — convert the hold into a booking",
             }.Select((step, index) => (step, index)))
    {
        await reasoning.AddStepAsync(trace.TraceId, stepNumber: index + 1, thought: step);
    }

    await reasoning.CompleteTraceAsync(
        trace.TraceId,
        outcome: "LookUpTraveller then CheckServiceBulletin then PlaceHold then Book",
        success: true);
    logger.LogInformation("    Recorded trace {TraceId} with 4 steps.", trace.TraceId);

    // ── Promote: an episode becomes a procedure ───────────────────────────────
    // Deliberately a separate call from CompleteTraceAsync. An Episode-kinded trace is
    // filtered OUT of procedure recall, so skipping this step leaves the agent with a
    // diary rather than a playbook — and the recall below would return nothing.
    var promoted = await reasoning.PromoteTraceAsync(trace.TraceId, TraceKind.Procedure);
    logger.LogInformation(
        "[2] Promoted to {Kind}. Only promoted traces answer a procedure query.",
        promoted?.Kind);

    // ── Run 2: the same task arrives again, in a fresh session ────────────────
    // A NEW session on purpose: procedural memory has to carry through the STORE.
    // Reusing the session would carry it in the context window instead, and the
    // demo would credit memory for what the transcript did.
    logger.LogInformation("[3] Second run — asking memory how this was done before.");

    var procedures = await reasoning.SearchSimilarTracesAsync(
        await embeddings.EmbedAsync(task),
        proceduresOnly: true,   // ← without this you get episodes: the wrong precedent library
        successFilter: true,    // ← never replay a method that failed
        limit: 3,
        minScore: 0.5,
        scope: owner);

    if (procedures.Count == 0)
    {
        logger.LogWarning(
            "    No procedure recalled. Expected exactly one. Check that the trace was promoted "
            + "and that TaskEmbedding was set — a trace without an embedding is invisible here.");
        return;
    }

    foreach (var procedure in procedures)
    {
        // The OUTCOME is the procedure. Rendering only the task would tell the agent it has done
        // this before and nothing about how — which was a real product gap until 2026-08-13.
        logger.LogInformation("    Recalled: {Outcome}", procedure.Outcome);
    }

    logger.LogInformation(
        "[4] The second run starts from that ordering instead of rediscovering it. "
        + "Measured on a task built to need it, this removes one tool call from every "
        + "attempt after the first (docs/reviews/procedural-benefit-result.md).");
}
