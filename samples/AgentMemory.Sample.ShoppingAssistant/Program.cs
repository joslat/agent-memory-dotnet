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
// This sample calls a REAL Azure OpenAI chat model and a REAL Azure OpenAI embedding model — no
// mocks. The agent decides on its own when to call the memory tools and the product tools; nothing
// here is scripted. Requires:
//   AZURE_OPENAI_ENDPOINT               (required, e.g. https://<resource>.openai.azure.com/)
//   AZURE_OPENAI_API_KEY                (required — no live-model fallback)
//   AZURE_OPENAI_DEPLOYMENT             (chat deployment name; default: gpt-4o-mini)
//   AZURE_OPENAI_EMBEDDING_DEPLOYMENT   (embedding deployment name; default: text-embedding-ada-002,
//                                        1536-dim — matches the Neo4j vector index default)
//   Neo4j__Uri      (default: bolt://localhost:7687)
//   Neo4j__Username (default: neo4j)
//   Neo4j__Password (default: password)
// =============================================================================

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
using AgentMemory.Sample.ShoppingAssistant;
using AgentMemory.Samples.Shared;

if (!RealAzureOpenAI.TryCreate(out var azureClient, out var chatDeployment, out var embeddingDeployment))
{
    RealAzureOpenAI.PrintMissingCredentials("AgentMemory for .NET — Shopping Assistant (MAF 1.9.0)");
    return;
}

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
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    azureClient.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator());
builder.Services.AddSingleton<IChatClient>(
    new MemoryTraceChatClient(azureClient.GetChatClient(chatDeployment).AsIChatClient()));
builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist             = true;
    options.ContextFormat.IncludeEntities    = true;
    options.ContextFormat.IncludeFacts       = true;
    options.ContextFormat.IncludePreferences = true;
});

using var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host;

await RunAsync(host.Services, chatDeployment, embeddingDeployment);

static async Task RunAsync(IServiceProvider root, string chatDeployment, string embeddingDeployment)
{
    Console.WriteLine("=== AgentMemory for .NET — Shopping Assistant (MAF 1.9.0) — live Azure OpenAI ===");
    Console.WriteLine($"    chat deployment: {chatDeployment}   embedding deployment: {embeddingDeployment}\n");

    await using var scope = root.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    var catalog = new ProductCatalog(sp.GetRequiredService<INeo4jTransactionRunner>());
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
        Console.WriteLine("    docker run -d --name neo4j -p 7474:7474 -p 7687:7687 -e NEO4J_AUTH=neo4j/password neo4j:5.26\n");
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
            Instructions =
                "You are a helpful retail shopping assistant. When the customer states a preference "
              + "(brand, budget, category), call remember_preference to persist it before replying. "
              + "Ground every product suggestion in the real catalog via search_products, "
              + "get_recommendations, get_related_products, and check_inventory — never invent a "
              + "product. At the start of a conversation, call recall_preferences or search_memory "
              + "to check what you already know about this customer before asking them to repeat "
              + "themselves.",
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

    // ── Session B — a NEW session for the same shopper still knows her ─────────
    Console.WriteLine(">> Session B — a brand-new session; memory is durable, so it still knows Amelia\n");
    var sessionB = (await agent.CreateSessionAsync())
        .WithMemoryIdentity(userId: shopper, sessionId: "cart-session-b", applicationId: "retail-demo");

    await SayAsync(agent, sessionB, ownerContext, shopper,
        "I'm back — remind me what I was after and suggest something.");

    Console.WriteLine("=== Done. Preferences + messages persist in Neo4j across sessions. ===");
}

// Runs one conversational turn against the REAL model. The ambient owner scope means any
// model-invoked memory tools stay scoped to this shopper. Prints every tool call the model made
// (name + a preview of its result) so the live function-calling loop is visible, then the reply.
static async Task SayAsync(AIAgent agent, AgentSession session, IWritableMemoryOwnerContext ownerContext,
    string userId, string message)
{
    SampleConsole.WriteUser(message);
    try
    {
        using (ownerContext.BeginOwnerScope(userId))
        {
            var response = await agent.RunAsync(message, session);

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count > 0)
            {
                var resultsByCallId = response.Messages.SelectMany(m => m.Contents)
                    .OfType<FunctionResultContent>()
                    .GroupBy(r => r.CallId)
                    .ToDictionary(g => g.Key, g => g.First());

                Console.WriteLine($"          [{calls.Count} tool call(s)]");
                foreach (var call in calls)
                {
                    var preview = resultsByCallId.TryGetValue(call.CallId, out var r)
                        ? (r.Result?.ToString() ?? "").Replace('\n', ' ').Replace('\r', ' ')
                        : "(no result)";
                    if (preview.Length > 140) preview = preview[..137] + "...";
                    SampleConsole.WriteToolCall(call.Name, preview);
                }
            }

            SampleConsole.WriteAssistant(response.Text);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"   [turn failed: {ex.Message}]\n");
    }
}
