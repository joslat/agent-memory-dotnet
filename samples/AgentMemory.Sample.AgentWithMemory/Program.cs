// =============================================================================
// AgentMemory for .NET — AgentWithMemory Sample (MAF 1.9.0)
//
// The .NET equivalent of the official Microsoft Agent Framework "memory" get-started sample
// (https://github.com/microsoft/agent-framework/tree/main/dotnet/samples/01-get-started/04_memory),
// backed by DURABLE Neo4j memory instead of session-scoped in-memory state.
//
// Full guide: docs/agent-framework.md — the .NET equivalent of the official (Python-only) Neo4j
// Memory Provider for Agent Framework (https://learn.microsoft.com/en-us/agent-framework/integrations/neo4j-memory).
//
// It demonstrates the full canonical "agent with memory" flow:
//   1. A ChatClientAgent with Neo4jMemoryContextProvider (an AIContextProvider) + memory tools.
//   2. A multi-turn AgentSession — messages are persisted to Neo4j after each turn.
//   3. Session serialize/restore (SerializeSessionAsync / DeserializeSessionAsync).
//   4. Durable cross-session recall — a brand-new session for the same owner/application still
//      sees prior memory, because the memory lives in Neo4j (not just in the session).
//
// Unlike the official sample's session-scoped ProviderSessionState, this memory survives process
// restarts and is scoped by the explicit store -> owner -> session identity below. This sample calls
// a REAL Azure OpenAI chat model and a REAL Azure OpenAI embedding model — no mocks. Requires:
//   AZURE_OPENAI_ENDPOINT               (required, e.g. https://<resource>.openai.azure.com/)
//   AZURE_OPENAI_API_KEY                (required — no live-model fallback)
//   AZURE_OPENAI_DEPLOYMENT             (chat deployment name; default: gpt-4o-mini)
//   AZURE_OPENAI_EMBEDDING_DEPLOYMENT   (embedding deployment name; default: text-embedding-ada-002)
//   Neo4j__Uri      (default: bolt://localhost:7687)
//   Neo4j__Username (default: neo4j)
//   Neo4j__Password (default: password)
// =============================================================================

using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Tools;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Samples.Shared;

if (!RealAzureOpenAI.TryCreate(out var azureClient, out var chatDeployment, out var embeddingDeployment))
{
    RealAzureOpenAI.PrintMissingCredentials("AgentMemory for .NET — AgentWithMemory Sample (MAF 1.9.0)");
    return;
}

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri      = builder.Configuration["Neo4j:Uri"]      ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
});
// StrictMultiTenant (#100) is the production-safe pattern for any deployment where more than one
// tenant's data lives in the same store: an operation that resolves with no owner scope now throws
// MemoryOwnerScopeRequiredException before touching Neo4j, instead of silently falling back to
// global/shared. Every call in this sample already runs inside ownerContext.BeginOwnerScope(userId)
// (see SafeRunAsync below), so flipping this on changes nothing for the golden path -- see step 4 for
// what happens when a call forgets to.
builder.Services.AddAgentMemoryCore(o => o.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant);
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

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host; // dispose the async-only Neo4j driver factory on exit
await RunAsync(host.Services);

static async Task RunAsync(IServiceProvider root)
{
    var logger = root.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== AgentMemory for .NET — AgentWithMemory Sample (MAF 1.9.0) ===");

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

    var memoryProvider = sp.GetRequiredService<Neo4jMemoryContextProvider>();
    var memoryTools = sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions();

    IChatClient chatClient = sp.GetRequiredService<IChatClient>();

    // WithMemoryOwnerScoping(sp) (#90) guarantees the owner scope spans the COMPLETE invocation -- passive
    // recall, the model call, the full tool-calling loop, and automatic persistence -- as one unbroken
    // async chain. This replaces manually wrapping every agent.RunAsync(...) call in
    // ownerContext.BeginOwnerScope(userId): a context-provider hook alone can't guarantee that (it
    // suspends on real I/O, so a value it sets does not reliably survive into tool calls that run after
    // it returns). Passing the IServiceProvider (rather than resolving IWritableMemoryOwnerContext
    // manually) also resolves the same AgentFrameworkOptions instance Neo4jMemoryContextProvider uses --
    // if a host ever customizes AgentFrameworkOptions.Default*Key, this keeps the wrapper reading identity
    // under the same StateBag keys the provider writes/reads, rather than silently drifting out of sync.
    AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "MemoryAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a friendly assistant with durable, graph-backed long-term memory.",
            Tools = [.. memoryTools],
        },
        AIContextProviders = [memoryProvider],
    }).WithMemoryOwnerScoping(sp);

    const string applicationId = "agent-memory-dotnet-golden-path";
    const string userId = "demo-user-ruaidhri";

    // ── 1. A multi-turn session — each turn is persisted to Neo4j ───────────────
    logger.LogInformation("\n>> Session A — teaching the agent some facts\n");
    var sessionA = (await agent.CreateSessionAsync()).WithMemoryIdentity(
        userId: userId,
        sessionId: "golden-path-session-a",
        conversationId: "golden-path-conversation-a",
        applicationId: applicationId);
    foreach (var turn in new[]
    {
        "Hi, my name is Ruaidhrí.",
        "I prefer window seats on flights.",
        "I work at Acme Corp.",
    })
    {
        SampleConsole.WriteUser(turn);
        SampleConsole.WriteAssistant(await SafeRunAsync(agent, turn, sessionA, logger));
    }

    // ── 2. Serialize and restore the session (canonical 04_memory feature) ──────
    logger.LogInformation("\n>> Serialize the session, then restore it and continue\n");
    JsonElement serialized = await agent.SerializeSessionAsync(sessionA);
    logger.LogInformation("Serialized session is {Bytes} bytes of JSON.", serialized.GetRawText().Length);
    var restored = (await agent.DeserializeSessionAsync(serialized)).WithMemoryIdentity(
        userId: userId,
        sessionId: "golden-path-session-a",
        conversationId: "golden-path-conversation-a",
        applicationId: applicationId);
    SampleConsole.WriteUser("What did I tell you?");
    SampleConsole.WriteAssistant(await SafeRunAsync(agent, "What did I tell you?", restored, logger));

    // ── 3. Durable cross-session recall ─────────────────────────────────────────
    // A brand-new session for the same owner/application still sees earlier memory, because recall is
    // scoped by user_id + application_id and the knowledge lives in Neo4j, not inside the MAF session.
    logger.LogInformation("\n>> Session B — a NEW session that still recalls durable memory\n");
    var sessionB = (await agent.CreateSessionAsync()).WithMemoryIdentity(
        userId: userId,
        sessionId: "golden-path-session-b",
        conversationId: "golden-path-conversation-b",
        applicationId: applicationId);
    SampleConsole.WriteUser("Remind me what you know about me.");
    SampleConsole.WriteAssistant(await SafeRunAsync(agent, "Remind me what you know about me.", sessionB, logger));

    // ── 4. StrictMultiTenant fails closed on a forgotten owner scope ────────────
    // Every recall above ran through the WithMemoryOwnerScoping-wrapped agent, so it was owner-scoped
    // automatically. This call deliberately bypasses that (it calls the context assembler directly,
    // skipping the agent entirely) -- with Isolation.Mode = StrictMultiTenant configured above, the
    // context assembler now throws MemoryOwnerScopeRequiredException instead of silently resolving to
    // global/shared memory.
    logger.LogInformation("\n>> Demonstrating StrictMultiTenant: an unscoped recall fails closed (bypassing the agent)\n");
    var contextAssembler = sp.GetRequiredService<IMemoryContextAssembler>();
    try
    {
        await contextAssembler.AssembleContextAsync(new RecallRequest
        {
            SessionId = "unscoped-demo-session",
            Query = "What do you know about me?",
            // No UserId set -- this is the "forgot to scope it" mistake StrictMultiTenant catches.
        });
        logger.LogWarning("    Expected a MemoryOwnerScopeRequiredException, got a result instead -- check Isolation.Mode.");
    }
    catch (MemoryOwnerScopeRequiredException ex)
    {
        logger.LogInformation("    Failed closed, as expected: {Message}", ex.Message);
    }

    logger.LogInformation("\n=== Demo complete. Memory persisted in Neo4j survives sessions and serialization. ===");
}

static async Task<string?> SafeRunAsync(AIAgent agent, string message, AgentSession session, ILogger logger)
{
    try
    {
        // Owner scoping (recall, tool calls, and persistence) is guaranteed automatically by the
        // WithMemoryOwnerScoping-wrapped agent (#90) -- no manual BeginOwnerScope needed here.
        var response = await agent.RunAsync(message, session);
        return response.Text;
    }
    catch (Exception ex)
    {
        logger.LogWarning("    Turn failed (likely no live Neo4j): {Message}", ex.Message);
        return null;
    }
}
