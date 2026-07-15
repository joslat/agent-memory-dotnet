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
// restarts and is scoped by the explicit store -> owner -> session identity below. Mock providers keep
// it runnable offline; replace the DI registrations with real MEAI providers for production inference.
//
//   Neo4j__Uri      (default: bolt://localhost:7687)
//   Neo4j__Username (default: neo4j)
//   Neo4j__Password (default: password)
// =============================================================================

using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
// Offline default: deterministic stub embeddings. Production hosts should replace this with a real
// MEAI provider, for example OpenAI/Azure OpenAI/Foundry embeddings with matching Neo4j dimensions.
builder.Services.TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>();
// Offline default: deterministic mock chat. Production hosts can replace IChatClient with any MEAI
// chat client; the rest of the sample wiring stays the same.
builder.Services.TryAddSingleton<IChatClient, EchoChatClient>();
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
    var ownerContext = sp.GetRequiredService<IWritableMemoryOwnerContext>();

    IChatClient chatClient = sp.GetRequiredService<IChatClient>();

    AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "MemoryAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a friendly assistant with durable, graph-backed long-term memory.",
            Tools = [.. memoryTools],
        },
        AIContextProviders = [memoryProvider],
    });

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
        logger.LogInformation("USER  : {Turn}", turn);
        logger.LogInformation("AGENT : {Text}", await SafeRunAsync(agent, turn, sessionA, logger, ownerContext, userId));
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
    logger.LogInformation("USER  : What did I tell you?");
    logger.LogInformation("AGENT : {Text}", await SafeRunAsync(agent, "What did I tell you?", restored, logger, ownerContext, userId));

    // ── 3. Durable cross-session recall ─────────────────────────────────────────
    // A brand-new session for the same owner/application still sees earlier memory, because recall is
    // scoped by user_id + application_id and the knowledge lives in Neo4j, not inside the MAF session.
    logger.LogInformation("\n>> Session B — a NEW session that still recalls durable memory\n");
    var sessionB = (await agent.CreateSessionAsync()).WithMemoryIdentity(
        userId: userId,
        sessionId: "golden-path-session-b",
        conversationId: "golden-path-conversation-b",
        applicationId: applicationId);
    logger.LogInformation("USER  : Remind me what you know about me.");
    logger.LogInformation("AGENT : {Text}", await SafeRunAsync(agent, "Remind me what you know about me.", sessionB, logger, ownerContext, userId));

    // ── 4. StrictMultiTenant fails closed on a forgotten owner scope ────────────
    // Every recall above ran inside ownerContext.BeginOwnerScope(userId). This call deliberately skips
    // that -- with Isolation.Mode = StrictMultiTenant configured above, the context assembler now throws
    // MemoryOwnerScopeRequiredException instead of silently resolving to global/shared memory.
    logger.LogInformation("\n>> Demonstrating StrictMultiTenant: an unscoped recall fails closed (no BeginOwnerScope)\n");
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

static async Task<string?> SafeRunAsync(
    AIAgent agent,
    string message,
    AgentSession session,
    ILogger logger,
    IWritableMemoryOwnerContext ownerContext,
    string userId)
{
    try
    {
        // The session state scopes provider recall/persistence. The ambient owner scope encloses the run
        // so any model-invoked memory tools inherit the same owner and cannot run against all owners.
        using (ownerContext.BeginOwnerScope(userId))
        {
            var response = await agent.RunAsync(message, session);
            return response.Text;
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning("    Turn failed (likely no live Neo4j): {Message}", ex.Message);
        return null;
    }
}

internal sealed class EchoChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        // Report how much prior context the memory provider injected, to make recall visible.
        var contextCount = messages.Count(m => m.Role != ChatRole.User);
        var lastUser = messages.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var reply = $"(mock LLM, {contextCount} context message(s) from memory) Re: \"{lastUser}\".";
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
