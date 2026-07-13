// =============================================================================
// AgentMemory for .NET — Shopping Assistant Sample (MAF 1.9.0)
//
// The .NET port of the official Neo4j Agent Memory "retail assistant" example for the Microsoft
// Agent Framework (https://github.com/neo4j-labs/agent-memory/tree/main/examples/microsoft_agent_retail_assistant,
// referenced from https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory).
//
// A shopping assistant that LEARNS a customer's preferences and RECOMMENDS products via graph
// traversal, backed by durable Neo4j memory. It wires the same pieces as the Python example:
//   • Neo4jMemoryContextProvider  (AIContextProvider)  — passive, bidirectional long-term memory
//   • MemoryToolFactory.CreateAIFunctions()            — the memory tools (create_memory_tools)
//   • ProductCatalog.CreateAIFunctions()               — the retail tools (get_product_tools)
//   • a retail system prompt, on a MAF ChatClientAgent
//
// Like every sample here it runs OFFLINE with a mock IChatClient (no API key). Because a mock model
// does not drive tool-calls, this demo SCRIPTS the shopping journey to exercise the product tools and
// memory directly. With a real IChatClient (OpenAI/Azure OpenAI) the agent invokes those same tools
// conversationally — see README.md; the memory wiring is identical.
//
//   Neo4j__Uri      (default: bolt://localhost:7687)
//   Neo4j__Username (default: neo4j)
//   Neo4j__Password (default: password)
// =============================================================================

using System.Runtime.CompilerServices;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Tools;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Sample.ShoppingAssistant;

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.SetMinimumLevel(LogLevel.Warning); // keep the demo output readable

builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri      = builder.Configuration["Neo4j:Uri"]      ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
});
builder.Services.AddAgentMemoryCore(_ => { });
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
// Offline defaults — replace with real Microsoft.Extensions.AI providers for production inference.
builder.Services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();
builder.Services.TryAddSingleton<IChatClient, ShoppingEchoChatClient>();
builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist             = true;
    options.ContextFormat.IncludeEntities    = true;
    options.ContextFormat.IncludeFacts       = true;
    options.ContextFormat.IncludePreferences = true;
});

using var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host;

await RunAsync(host.Services);

static async Task RunAsync(IServiceProvider root)
{
    Console.WriteLine("=== AgentMemory for .NET — Shopping Assistant (MAF 1.9.0) ===\n");

    await using var scope = root.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    var catalog = new ProductCatalog(sp.GetRequiredService<INeo4jTransactionRunner>());
    var facade = sp.GetRequiredService<IMemoryQueryFacade>();
    var ownerContext = sp.GetRequiredService<IWritableMemoryOwnerContext>();

    // ── Setup: Neo4j schema + sample product graph ─────────────────────────────
    try
    {
        await sp.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
        await catalog.SeedAsync();
        Console.WriteLine("Neo4j schema ready; 10 sample products loaded.\n");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[!] No live Neo4j ({ex.Message}). Start one with:");
        Console.WriteLine("    docker run -d --name neo4j -p 7687:7687 -e NEO4J_AUTH=neo4j/password neo4j:5.26\n");
        return;
    }

    // ── Build the shopping-assistant agent (context provider + memory tools + product tools) ────
    var memoryProvider = sp.GetRequiredService<Neo4jMemoryContextProvider>();
    var memoryTools    = sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions();
    var productTools   = catalog.CreateAIFunctions();

    AIAgent agent = sp.GetRequiredService<IChatClient>().AsAIAgent(new ChatClientAgentOptions
    {
        Name = "ShoppingAssistant",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a helpful shopping assistant. Learn and remember the customer's "
                         + "preferences (brands, budget, categories) and recommend products that fit.",
            Tools = [.. memoryTools, .. productTools],
        },
        AIContextProviders = [memoryProvider],
    });

    const string shopper = "shopper-amelia";

    // ── Session A — the customer shops and states preferences ──────────────────
    Console.WriteLine(">> Session A — Amelia is shopping for running shoes\n");
    var sessionA = (await agent.CreateSessionAsync())
        .WithMemoryIdentity(userId: shopper, sessionId: "cart-session-a", applicationId: "retail-demo");

    await SayAsync(agent, sessionA, ownerContext, shopper,
        "Hi! I'm looking for running shoes. I love Nike and want to stay under $150.");

    // The customer stated preferences — persist them (a real model would call `remember_preference`).
    await facade.RememberPreferenceAsync("Prefers the Nike brand", "brand");
    await facade.RememberPreferenceAsync("Budget around $150", "budget");
    Console.WriteLine("   [memory] saved preferences: brand=Nike, budget≈$150\n");

    // Retail tools over the product graph (a real model would call these; here we call them directly).
    Console.WriteLine(await catalog.SearchProductsAsync("running shoes", brand: "Nike", maxPrice: 150));
    Console.WriteLine();
    Console.WriteLine(await catalog.GetRecommendationsAsync(preferredBrand: "Nike", category: "shoes"));
    Console.WriteLine();
    Console.WriteLine(await catalog.GetRelatedProductsAsync("Nike Air Zoom Pegasus 40"));
    Console.WriteLine();
    Console.WriteLine(await catalog.CheckInventoryAsync("Asics Gel-Kayano 31"));
    Console.WriteLine();

    // ── Session B — a NEW session for the same shopper still knows her ─────────
    Console.WriteLine(">> Session B — a brand-new session; memory is durable, so it still knows Amelia\n");
    var sessionB = (await agent.CreateSessionAsync())
        .WithMemoryIdentity(userId: shopper, sessionId: "cart-session-b", applicationId: "retail-demo");

    await SayAsync(agent, sessionB, ownerContext, shopper,
        "I'm back — remind me what I was after and suggest something.");

    // The stored preference drives the recommendation across sessions.
    Console.WriteLine("   [memory] recalling saved preferences:\n" + (await facade.SearchMemoryAsync("preferences")).Text);
    Console.WriteLine();
    Console.WriteLine(await catalog.GetRecommendationsAsync(preferredBrand: "Nike"));

    Console.WriteLine("\n=== Done. Preferences + messages persist in Neo4j across sessions. ===");
    Console.WriteLine("Tip: with a real IChatClient (OpenAI/Azure), the agent calls these same memory + "
                    + "product tools itself — the wiring above is unchanged. See README.md.");
}

// Runs one conversational turn. The ambient owner scope means any model-invoked memory tools stay
// scoped to this shopper. With the mock client the reply reports how much memory context was injected.
static async Task SayAsync(AIAgent agent, AgentSession session, IWritableMemoryOwnerContext ownerContext,
    string userId, string message)
{
    Console.WriteLine($"USER      : {message}");
    try
    {
        using (ownerContext.BeginOwnerScope(userId))
        {
            var response = await agent.RunAsync(message, session);
            Console.WriteLine($"ASSISTANT : {response.Text}\n");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   [turn failed: {ex.Message}]\n");
    }
}

/// <summary>
/// Offline mock chat client — reports how much memory context the provider injected, so recall is
/// visible without a real model. Swap for a real <see cref="IChatClient"/> to get conversational
/// tool-calling.
/// </summary>
internal sealed class ShoppingEchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var contextCount = messages.Count(m => m.Role != ChatRole.User);
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var reply = $"(mock assistant — {contextCount} memory context message(s) recalled) "
                  + $"Happy to help with: \"{lastUser}\".";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken).ConfigureAwait(false);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
