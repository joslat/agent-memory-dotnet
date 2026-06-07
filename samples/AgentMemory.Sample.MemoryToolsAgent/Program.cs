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
// They are registered on a ChatClientAgent (ChatOptions.Tools) so a real LLM would call them
// autonomously. To keep the sample runnable offline (no API key / no function-calling LLM), it
// also invokes a few tools directly to show they execute against Neo4j memory.
//
//   Neo4j__Uri      (default: bolt://localhost:7687)
//   Neo4j__Username (default: neo4j)
//   Neo4j__Password (default: password)
// =============================================================================

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
builder.Services.AddAgentMemoryFramework(_ => { });

var host = builder.Build();
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
    IChatClient chatClient = new EchoChatClient();
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

    // 3) A normal agent turn still works (the mock client doesn't call tools, but a real one would).
    var session = await agent.CreateSessionAsync();
    var response = await agent.RunAsync("Remember that I prefer aisle seats too.", session);
    logger.LogInformation("\nAGENT : {Text}", response.Text);

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

internal sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var toolCount = options?.Tools?.Count ?? 0;
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var reply = $"(mock LLM, {toolCount} tools available) Noted: \"{lastUser}\".";
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
