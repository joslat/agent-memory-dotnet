// =============================================================================
// AgentMemory for .NET — ChatHistoryProvider Sample (MAF 1.9.0)
//
// Demonstrates wiring Neo4jChatHistoryProvider into a Microsoft Agent Framework agent via
// ChatClientAgentOptions.ChatHistoryProvider. Unlike an AIContextProvider (long-term memory),
// a ChatHistoryProvider manages per-session conversation history: it loads recent messages from
// Neo4j before each turn and persists request/response messages after each turn.
//
// This sample calls a REAL Azure OpenAI chat model and a REAL Azure OpenAI embedding model — no
// mocks. Memory degrades gracefully without Neo4j. Requires:
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
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Samples.Shared;

if (!RealAzureOpenAI.TryCreate(out var azureClient, out var chatDeployment, out var embeddingDeployment))
{
    RealAzureOpenAI.PrintMissingCredentials("AgentMemory for .NET — ChatHistoryProvider Sample (MAF 1.9.0)");
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
builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist = false; // history persistence only; no extraction in this sample
    options.ContextFormat.MaxChatHistoryMessages = 10;
});

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host; // dispose the async-only Neo4j driver factory on exit
await RunAsync(host.Services);

static async Task RunAsync(IServiceProvider root)
{
    var logger = root.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== AgentMemory for .NET — ChatHistoryProvider Sample (MAF 1.9.0) ===");

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

    // The Neo4j-backed ChatHistoryProvider: loads recent history before each turn, persists after.
    var historyProvider = sp.GetRequiredService<Neo4jChatHistoryProvider>();

    IChatClient chatClient = sp.GetRequiredService<IChatClient>();

    AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "HistoryAgent",
        ChatOptions = new ChatOptions { Instructions = "You are a helpful assistant." },
        ChatHistoryProvider = historyProvider,
    });

    var session = await agent.CreateSessionAsync();

    string[] turns =
    [
        "My favorite language is C#.",
        "I'm building an AI agent with persistent memory.",
        "Recap what we've discussed.",
    ];

    foreach (var turn in turns)
    {
        SampleConsole.WriteUser(turn);
        try
        {
            var response = await agent.RunAsync(turn, session);
            SampleConsole.WriteAssistant(response.Text);
        }
        catch (Exception ex)
        {
            logger.LogWarning("    Turn failed (likely no live Neo4j): {Message}", ex.Message);
        }
    }

    logger.LogInformation("=== Demo complete. Conversation history was persisted to Neo4j. ===");
}
