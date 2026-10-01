// =============================================================================
// AgentMemory for .NET — Conversational Agent Sample (the Conversational preset)
//
// An agent that learns from what the person says and keeps it current:
//   • MemoryOptions.CreateConversational(), LlmExtractionOptions.ApplyConversational() and
//     AgentFrameworkOptions.ApplyConversational(): one call per package
//   • memory LEARNED from the conversation by a model (no memory tools; nothing stored by hand)
//   • three sessions: the person tells, then changes their mind, then asks
//   • what memory holds at the end: what is current, and what it replaced
//
// Requires:
//   ONE inference provider, auto-detected: Azure OpenAI, Bitdeer, OpenAI, Foundry, or any
//   OpenAI-compatible host. The shortest is BITDEER_API_KEY, which gets chat and embeddings.
//   Name one explicitly with AI_INFERENCE_PROVIDER; see docs/configuration/inference-providers.md.
//   A live Neo4j: Neo4j__Uri (default bolt://localhost:7687), Neo4j__Username, Neo4j__Password.
// =============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using AgentMemory;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Samples.Shared;

if (!RealModel.TryCreate(out var chatClient, out var embeddingGenerator, out var modelSettings))
{
    RealModel.PrintMissingProvider("AgentMemory for .NET — Conversational Agent Sample");
    return;
}

RealModel.PrintModelBanner(modelSettings);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton(chatClient);              // the agent's model, and the one extraction asks
builder.Services.AddSingleton(embeddingGenerator);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();

// ── The Conversational preset: one call per package ─────────────────────────
builder.Services.AddNeo4jAgentMemory(
    MemoryOptions.CreateConversational(),
    neo4j =>
    {
        neo4j.Uri = builder.Configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
        neo4j.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
        neo4j.Password = builder.Configuration["Neo4j:Password"] ?? "password";
        // The store's vector width comes from the resolved embedding model (1024 on Bitdeer, 1536 on ada-002).
        neo4j.EmbeddingDimensions = modelSettings.EmbeddingDimensions!.Value;
    },
    llm => llm.ApplyConversational());
builder.Services.AddAgentMemoryFramework(options => options.ApplyConversational());

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host;

await using var scope = host.Services.CreateAsyncScope();
var sp = scope.ServiceProvider;
await sp.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

// The preset isolates owners strictly, so every call runs as one person. WithMemoryOwnerScoping keeps that
// owner around recall, the model call and the memorising after it. A fresh person per run, so reruns start clean.
var person = $"conversational-sample-{Guid.NewGuid():N}"[..34];
AIAgent agent = chatClient
    .AsAIAgent(new ChatClientAgentOptions
    {
        Name = "Mem",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a friendly assistant who remembers the person you talk to. Answer briefly.",
        },
        AIContextProviders = [sp.GetRequiredService<Neo4jMemoryContextProvider>()],
        ChatHistoryProvider = AgentMemoryChatHistory.CreateInMemoryProvider(),
    })
    .WithMemoryOwnerScoping(sp);
var memorising = sp.GetRequiredService<IBackgroundExtraction>();

async Task<AgentSession> SessionAsync(string id) =>
    (await agent.CreateSessionAsync()).WithMemoryIdentity(userId: person, sessionId: $"{person}-{id}", applicationId: "conversational-sample");

async Task SayAsync(AgentSession session, string text)
{
    Console.WriteLine($"\nYou:  {text}");
    var reply = await agent.RunAsync(text, session);
    Console.WriteLine($"Mem:  {reply.Text}");
}

Console.WriteLine("\n── Session 1: Lena tells Mem about herself");
var first = await SessionAsync("1");
await SayAsync(first, "Hi, I'm Lena. I live in Lyon and I'm training for the half marathon in April.");
await SayAsync(first, "My manager is Priya. Her husband Daniel is a chef.");
await memorising.WhenIdleAsync();   // memorising runs after each answer; a new session should find it done

Console.WriteLine("\n── Session 2: she changes her mind");
var second = await SessionAsync("2");
await SayAsync(second, "Big news: I moved to Copenhagen last week. And I'm doing the full marathon in May instead of the half in April.");
await memorising.WhenIdleAsync();

Console.WriteLine("\n── Session 3: a new conversation asks");
var third = await SessionAsync("3");
await SayAsync(third, "Where do I live now, and what am I training for?");
await SayAsync(third, "What does my manager's husband do?");
await memorising.WhenIdleAsync();

// ── What memory holds: current, and what each change of mind replaced ───────
Console.WriteLine("\n── What memory holds about Lena");
var facts = await sp.GetRequiredService<ILongTermMemoryService>()
    .GetFactsBySubjectAsync("Lena", MemoryScope.For(person, includeShared: false));
foreach (var fact in facts.OrderBy(f => f.InvalidatedAtUtc is not null).ThenBy(f => f.CreatedAtUtc))
{
    var state = fact.InvalidatedAtUtc is null && (fact.ValidUntil is null || fact.ValidUntil > DateTimeOffset.UtcNow)
        ? "current "
        : "replaced";
    Console.WriteLine($"  [{state}] {fact.Subject} | {fact.Predicate} | {fact.Object}");
}
