// =============================================================================
// AgentMemory for .NET — MemoryToolsAgent Sample (MAF 1.9.0)
//
// Demonstrates the memory TOOLS an agent uses to explicitly read/write memory — the .NET
// equivalent of the official Neo4j Agent Memory `create_memory_tools(memory)` pattern
// (https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory).
//
// MemoryToolFactory.CreateAIFunctions() returns the six memory tools as MEAI AIFunctions:
//   search_memory · remember_preference · remember_fact · recall_preferences ·
//   search_knowledge · find_similar_tasks
//
// They are registered on a ChatClientAgent (ChatOptions.Tools) so a real LLM calls them
// autonomously. This sample also invokes a few tools directly first, to show the tool mechanics
// executing against real Neo4j memory independent of the model. This sample calls a REAL Azure
// OpenAI chat model and a REAL Azure OpenAI embedding model — no mocks. Requires:
//   AZURE_OPENAI_ENDPOINT               (required, e.g. https://<resource>.openai.azure.com/)
//   AZURE_OPENAI_API_KEY                (required — no live-model fallback)
//   AZURE_OPENAI_DEPLOYMENT             (chat deployment name; default: gpt-4o-mini)
//   AZURE_OPENAI_EMBEDDING_DEPLOYMENT   (embedding deployment name; default: text-embedding-ada-002)
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
using AgentMemory.Samples.Shared;

if (!RealAzureOpenAI.TryCreate(out var azureClient, out var chatDeployment, out var embeddingDeployment))
{
    RealAzureOpenAI.PrintMissingCredentials("AgentMemory for .NET — MemoryToolsAgent Sample (MAF 1.9.0)");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

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
builder.Services.AddAgentMemoryFramework(_ => { });

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host; // dispose the async-only Neo4j driver factory on exit
await RunAsync(host.Services);

static async Task RunAsync(IServiceProvider root)
{
    var logger = root.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== AgentMemory for .NET — MemoryToolsAgent Sample (MAF 1.9.0) ===");

    await using var scope = root.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    try
    {
        await sp.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
        logger.LogInformation("Neo4j schema ready.");
    }
    catch (Exception ex)
    {
        logger.LogWarning("Schema bootstrap skipped (no live Neo4j): {Message}", ex.Message);
    }

    // The six memory tools (the create_memory_tools(memory) equivalent).
    var memoryTools = sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions();

    // 1) Register them on a real agent exactly as an app would — a function-calling LLM picks
    //    these up from ChatOptions.Tools and calls them autonomously.
    IChatClient chatClient = sp.GetRequiredService<IChatClient>();
    AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "MemoryToolsAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are an assistant that can remember and recall user facts and preferences.",
            Tools = [.. memoryTools],
        },
    });
    logger.LogInformation("Agent '{Name}' registered with {Count} memory tools:", "MemoryToolsAgent", memoryTools.Count);
    foreach (var fn in memoryTools)
        logger.LogInformation("  • {Name} — {Description}", fn.Name, fn.Description);

    // 2) Drive the tools directly so the sample demonstrates real memory I/O without an LLM.
    logger.LogInformation("\n[direct tool invocation against Neo4j]");
    await InvokeToolAsync(memoryTools, "remember_preference",
        new() { ["preferenceText"] = "prefers window seats on flights", ["category"] = "travel" }, logger);
    await InvokeToolAsync(memoryTools, "remember_fact",
        new() { ["subject"] = "Alice", ["predicate"] = "works_at", ["object"] = "Acme Corp" }, logger);
    await InvokeToolAsync(memoryTools, "search_memory",
        new() { ["query"] = "What do we know about Alice's travel preferences?" }, logger);

    // 3) A normal agent turn — the real model decides on its own whether to call a memory tool.
    var session = await agent.CreateSessionAsync();
    const string turn = "Remember that I prefer aisle seats too.";
    SampleConsole.WriteUser(turn);
    var response = await agent.RunAsync(turn, session);
    foreach (var call in response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>())
        SampleConsole.WriteToolCall(call.Name, "(called by the live model)");
    SampleConsole.WriteAssistant(response.Text);

    logger.LogInformation("=== Demo complete. ===");
}

static async Task InvokeToolAsync(
    IReadOnlyList<AIFunction> tools, string name, Dictionary<string, object?> args, ILogger logger)
{
    var tool = tools.First(t => t.Name == name);
    try
    {
        var result = await tool.InvokeAsync(new AIFunctionArguments(args));
        logger.LogInformation("  {Name}({Args}) -> {Result}", name, string.Join(", ", args.Keys), result);
    }
    catch (Exception ex)
    {
        logger.LogWarning("  {Name} failed (likely no live Neo4j): {Message}", name, ex.Message);
    }
}
