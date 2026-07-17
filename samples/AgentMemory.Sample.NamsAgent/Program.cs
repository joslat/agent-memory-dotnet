// =============================================================================
// AgentMemory for .NET — NamsAgent Sample (MAF 1.9.0)
//
// The NAMS-backed equivalent of AgentWithMemory: a ChatClientAgent whose memory lives in the real
// NAMS SaaS (https://memory.neo4jlabs.com) instead of a direct Neo4j connection.
//
// It demonstrates:
//   1. A ChatClientAgent with NamsMemoryContextProvider (an AIContextProvider).
//   2. A multi-turn AgentSession — each turn is persisted to NAMS after the run.
//   3. Session serialize/restore (SerializeSessionAsync / DeserializeSessionAsync).
//   4. Durable cross-session recall — a brand-new session for the same user/application still sees
//      prior memory, because NAMS — not the MAF session — is where the conversation actually lives.
//   5. Sanitized recall diagnostics via the shared MemoryTraceChatClient, which prints the
//      <recalled_memory> blocks NamsMemoryContextProvider injects before each live model call.
//
// Unlike the direct Neo4j samples, there is no local embedding generator, schema bootstrapper, or
// memory tools to wire up — NAMS performs extraction/embedding/reflection server-side, and no MCP
// tool surface for NAMS exists yet (that's engineering plan Phase 8, not built). This sample calls a
// REAL Azure OpenAI chat model — no mock. Requires:
//   AZURE_OPENAI_ENDPOINT   (required, e.g. https://<resource>.openai.azure.com/)
//   AZURE_OPENAI_API_KEY    (required — no live-model fallback)
//   AZURE_OPENAI_DEPLOYMENT (chat deployment name; default: gpt-4o-mini)
//   NAMS_API_KEY            (required — your NAMS SaaS API key)
//   NAMS_WORKSPACE_ID       (optional — only needed for an account-wide/admin key; a
//                            workspace-scoped key already carries its workspace implicitly)
// =============================================================================

using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Nams;
using AgentMemory.Nams;
using AgentMemory.Samples.Shared;

if (!RealAzureOpenAI.TryCreate(out var azureClient, out var chatDeployment, out _))
{
    RealAzureOpenAI.PrintMissingCredentials("AgentMemory for .NET — NamsAgent Sample (MAF 1.9.0)");
    return;
}

var namsApiKey = Environment.GetEnvironmentVariable("NAMS_API_KEY");
if (string.IsNullOrWhiteSpace(namsApiKey))
{
    Console.WriteLine("=== AgentMemory for .NET — NamsAgent Sample (MAF 1.9.0) ===\n");
    Console.WriteLine("[!] NAMS is not configured. This sample calls the real NAMS SaaS — there is no");
    Console.WriteLine("    offline fallback. Set this and re-run:");
    Console.WriteLine("      NAMS_API_KEY        (required — your NAMS SaaS API key)");
    Console.WriteLine("      NAMS_WORKSPACE_ID   (optional — only needed for an account-wide/admin key)");
    return;
}
var namsWorkspaceId = Environment.GetEnvironmentVariable("NAMS_WORKSPACE_ID");

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddNamsAgentMemory(o =>
{
    o.Endpoint = NamsWellKnown.Endpoint;
    o.ApiKey = namsApiKey;
    o.WorkspaceId = namsWorkspaceId;
});
// AddNamsAgentMemoryFramework's own documented precondition: AddAgentMemoryFramework() must be
// registered too (it owns ContextFormatOptions/AgentFrameworkOptions, which the NAMS provider reads).
builder.Services.AddAgentMemoryFramework();
builder.Services.AddNamsAgentMemoryFramework();
builder.Services.AddSingleton<IChatClient>(
    new MemoryTraceChatClient(azureClient.GetChatClient(chatDeployment).AsIChatClient()));

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host; // dispose the DI container (HttpClient, etc.) on exit
await RunAsync(host.Services);

static async Task RunAsync(IServiceProvider root)
{
    var logger = root.GetRequiredService<ILogger<Program>>();
    logger.LogInformation("=== AgentMemory for .NET — NamsAgent Sample (MAF 1.9.0) ===");

    await using var scope = root.CreateAsyncScope();
    var sp = scope.ServiceProvider;

    var memoryProvider = sp.GetRequiredService<NamsMemoryContextProvider>();
    IChatClient chatClient = sp.GetRequiredService<IChatClient>();

    // No .WithMemoryOwnerScoping(sp) here -- that wrapper exists to keep an ambient AsyncLocal owner
    // context alive across a LOCAL tool-calling loop (#90), which only matters for the direct Neo4j
    // path's memory tools. NamsMemoryContextProvider reads identity directly off the session's state
    // bag (no ambient context, no local tools yet), so the session identity below is sufficient alone.
    AIAgent agent = chatClient.AsAIAgent(new ChatClientAgentOptions
    {
        Name = "NamsMemoryAgent",
        ChatOptions = new ChatOptions
        {
            Instructions = "You are a friendly assistant with durable, NAMS-backed long-term memory.",
        },
        AIContextProviders = [memoryProvider],
    });

    const string applicationId = "agent-memory-dotnet-nams-sample";
    const string userId = "demo-user-nams";

    // ── 1. A multi-turn session — each turn is persisted to NAMS ────────────────
    logger.LogInformation("\n>> Session A — teaching the agent some facts\n");
    var sessionA = (await agent.CreateSessionAsync()).WithMemoryIdentity(
        userId: userId,
        sessionId: "nams-sample-session-a",
        conversationId: "nams-sample-conversation-a",
        applicationId: applicationId);
    foreach (var turn in new[]
    {
        "Hi, my name is Ada.",
        "I prefer dark mode in every app I use.",
        "I work at Contoso as a data engineer.",
    })
    {
        SampleConsole.WriteUser(turn);
        SampleConsole.WriteAssistant(await SafeRunAsync(agent, turn, sessionA, logger));
        // NAMS ingests/extracts asynchronously server-side (the plan's own "eventual consistency"
        // characteristic -- see NamsLiveConnectivityTests' bounded polling for the same reason). A
        // recall that fires immediately after persisting can race ahead of NAMS's own indexing; a
        // short pause between turns lets it catch up so later recall actually reflects what was just
        // said. Not needed if turns are naturally paced (e.g. a real user typing).
        await Task.Delay(TimeSpan.FromSeconds(3));
    }

    // ── 2. Serialize and restore the session ────────────────────────────────────
    logger.LogInformation("\n>> Serialize the session, then restore it and continue\n");
    var serialized = await agent.SerializeSessionAsync(sessionA);
    logger.LogInformation("Serialized session is {Bytes} bytes of JSON.", serialized.GetRawText().Length);
    var restored = (await agent.DeserializeSessionAsync(serialized)).WithMemoryIdentity(
        userId: userId,
        sessionId: "nams-sample-session-a",
        conversationId: "nams-sample-conversation-a",
        applicationId: applicationId);
    SampleConsole.WriteUser("What did I tell you?");
    SampleConsole.WriteAssistant(await SafeRunAsync(agent, "What did I tell you?", restored, logger));

    // ── 3. Durable cross-session recall ─────────────────────────────────────────
    // A brand-new session for the same user/application still sees earlier memory, because NAMS --
    // not the MAF session -- is where the conversation and its extracted memory actually live.
    logger.LogInformation("\n>> Session B — a NEW session that still recalls durable memory\n");
    var sessionB = (await agent.CreateSessionAsync()).WithMemoryIdentity(
        userId: userId,
        sessionId: "nams-sample-session-b",
        conversationId: "nams-sample-conversation-b",
        applicationId: applicationId);
    SampleConsole.WriteUser("Remind me what you know about me.");
    SampleConsole.WriteAssistant(await SafeRunAsync(agent, "Remind me what you know about me.", sessionB, logger));

    logger.LogInformation("\n=== Demo complete. Memory persisted in NAMS survives sessions and serialization. ===");
}

static async Task<string?> SafeRunAsync(AIAgent agent, string message, AgentSession session, ILogger logger)
{
    try
    {
        var response = await agent.RunAsync(message, session);
        return response.Text;
    }
    catch (Exception ex)
    {
        logger.LogWarning("    Turn failed (check NAMS credentials/connectivity): {Message}", ex.Message);
        return null;
    }
}
