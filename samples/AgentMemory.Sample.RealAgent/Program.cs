// =============================================================================
// AgentMemory for .NET — Real MAF Agent Sample
//
// Demonstrates a REAL Microsoft Agent Framework agent (ChatClientAgent) wired with:
//   • Neo4jMemoryContextProvider  — injects long-term memory before each turn and
//                                    persists messages after each turn (AIContextProvider)
//   • MemoryToolFactory tools      — the six memory AIFunctions, callable by the agent
//   • native MAF OpenTelemetry     — agent.AsBuilder().UseOpenTelemetry()
//   • multi-turn AgentSession      — memory correlates across turns
//
// A mock IChatClient (EchoChatClient) is used so the sample runs offline with no API key.
// Memory operations degrade gracefully when no live Neo4j is available (warnings only).
//
//   Neo4j__Uri      (default: bolt://localhost:7687)
//   Neo4j__Username (default: neo4j)
//   Neo4j__Password (default: password)
// =============================================================================

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Tools;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;

var builder = Host.CreateApplicationBuilder(args);

// ── 1. Neo4j + core memory + MAF adapter ────────────────────────────────────
builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri      = builder.Configuration["Neo4j:Uri"]      ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
});
builder.Services.AddAgentMemoryCore(_ => { });
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();
builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist             = true;
    options.ContextFormat.IncludeEntities    = true;
    options.ContextFormat.IncludeFacts       = true;
    options.ContextFormat.IncludePreferences = true;
});

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host; // dispose the async-only Neo4j driver factory on exit

// ── OpenTelemetry: a console ActivityListener for the agent's spans ──────────
// UseOpenTelemetry() below emits MAF agent activities; in production add a real exporter.
using var listener = new ActivityListener
{
    ShouldListenTo  = _ => true,
    Sample          = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
    ActivityStarted = a => Console.WriteLine($"  [OTel] ▶ {a.OperationName}"),
    ActivityStopped = a => Console.WriteLine($"  [OTel] ■ {a.OperationName} ({a.Duration.TotalMilliseconds:F0} ms)"),
};
ActivitySource.AddActivityListener(listener);

await RunAsync(host.Services);

static async Task RunAsync(IServiceProvider root)
{
    var logger = root.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== AgentMemory for .NET — Real MAF Agent Sample ===");

    await using var scope = root.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    // Ensure the Neo4j schema (constraints + vector indexes) exists so memory recall works.
    // Degrades gracefully when no live Neo4j is available.
    try
    {
        await sp.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
        logger.LogInformation("Neo4j schema ready.");
    }
    catch (Exception ex)
    {
        logger.LogWarning("Schema bootstrap skipped (no live Neo4j): {Message}", ex.Message);
    }

    // The MAF context provider (long-term memory) and the memory tools.
    var memoryProvider = sp.GetRequiredService<Neo4jMemoryContextProvider>();
    var memoryTools = sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions();

    // A mock chat client keeps the sample runnable offline. Swap for a real IChatClient
    // (e.g. an OpenAI/Azure client) to get genuine LLM responses and tool calls.
    IChatClient chatClient = new EchoChatClient();

    // Build a real ChatClientAgent: memory provider runs before/after each turn, memory tools
    // are offered to the model, and native MAF OpenTelemetry wraps the agent.
    AIAgent agent = chatClient
        .AsAIAgent(new ChatClientAgentOptions
        {
            Name = "MemoryAgent",
            ChatOptions = new ChatOptions
            {
                Instructions =
                    "You are a helpful assistant with long-term memory. " +
                    "Use the memory tools to remember and recall the user's facts and preferences.",
                Tools = [.. memoryTools],
            },
            AIContextProviders = [memoryProvider],
        })
        .AsBuilder()
        .UseOpenTelemetry()
        .Build();

    // One session carries state across turns; the memory provider correlates memory by the
    // agent identity, so facts learned in turn 1 are available in later turns.
    var session = await agent.CreateSessionAsync();

    string[] turns =
    [
        "Hi, my name is Alice and I prefer dark mode.",
        "Please remember that I work at Acme Corp.",
        "What do you know about me so far?",
    ];

    foreach (var turn in turns)
    {
        logger.LogInformation("USER  : {Turn}", turn);
        try
        {
            var response = await agent.RunAsync(turn, session);
            logger.LogInformation("AGENT : {Text}", response.Text);
        }
        catch (Exception ex)
        {
            logger.LogWarning("    Turn failed (likely no live Neo4j): {Message}", ex.Message);
        }
    }

    logger.LogInformation("=== Demo complete. ===");
}

// =============================================================================
// Mock IChatClient — deterministic, offline. Replace with a real provider for genuine
// inference and tool-calling.
// =============================================================================
internal sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var reply = $"(mock LLM) Understood: \"{lastUser}\". I'll keep that in mind.";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose() { }
}
