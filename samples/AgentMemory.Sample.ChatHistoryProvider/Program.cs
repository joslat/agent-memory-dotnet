// =============================================================================
// AgentMemory for .NET — ChatHistoryProvider Sample (MAF 1.9.0)
//
// Demonstrates wiring Neo4jChatHistoryProvider into a Microsoft Agent Framework agent via
// ChatClientAgentOptions.ChatHistoryProvider. Unlike an AIContextProvider (long-term memory),
// a ChatHistoryProvider manages per-session conversation history: it loads recent messages from
// Neo4j before each turn and persists request/response messages after each turn.
//
// A mock IChatClient keeps the sample runnable offline. Memory degrades gracefully without Neo4j.
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
builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist = false; // history persistence only; no extraction in this sample
    options.ContextFormat.MaxContextMessages = 10;
});

var host = builder.Build();
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

    IChatClient chatClient = new EchoChatClient();

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

    logger.LogInformation("=== Demo complete. Conversation history was persisted to Neo4j. ===");
}

internal sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Echo the count of prior history messages to show the provider is injecting them.
        var historyCount = messages.Count(m => m.Role != ChatRole.System);
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var reply = $"(mock LLM) I see {historyCount} message(s) of context. You said: \"{lastUser}\".";
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
