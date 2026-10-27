// =============================================================================
// AgentMemory for .NET — CopilotAgent Sample (MAF 1.23 + GitHub Copilot SDK)
//
// Durable memory for an agent whose model loop we do not own: Microsoft Agent Framework's GitHubCopilotAgent drives
// the GitHub Copilot CLI, which runs its own tool loop in another process. Memory reaches it two ways:
//
//   1. Around each outer turn. GitHubCopilotAgent is an AIAgent but not a ChatClientAgent, so it takes only
//      message-level context (AIAgentBuilder.UseAIContextProviders takes a MessageAIContextProvider). MemoryMessages
//      below adapts Neo4jMemoryContextProvider to that: the same recall before the turn, the same persistence and
//      extraction after it. Instructions and tools a provider adds have no way in.
//   2. Inside Copilot's loop. The SDK's OnUserPromptSubmitted hook fires for each prompt the session takes; it
//      recalls for that prompt and returns the memory as AdditionalContext.
//
// The sample counts both, so it answers whether memory is injected once per outer turn or also inside the loop.
//
// The model is BYOK: the Copilot runtime calls your OpenAI-compatible provider (Bitdeer by default), so no Copilot
// subscription is used. Requires:
//   - the GitHub Copilot CLI on PATH (`copilot --version`), or COPILOT_CLI_PATH;
//   - ONE inference provider, auto-detected as in every sample (BITDEER_API_KEY is the shortest);
//   - Neo4j__Uri / Neo4j__Username / Neo4j__Password (defaults bolt://localhost:7687, neo4j, password).
// =============================================================================

using GitHub.Copilot;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Extraction.Llm;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Samples.Shared;

if (!RealModel.TryCreate(out var chatClient, out var embeddingGenerator, out var modelSettings))
{
    RealModel.PrintMissingProvider("AgentMemory for .NET — CopilotAgent Sample");
    return 0;
}
RealModel.PrintModelBanner(modelSettings);

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddNeo4jAgentMemory(options =>
{
    options.Uri = builder.Configuration["Neo4j:Uri"] ?? "bolt://localhost:7687";
    options.Username = builder.Configuration["Neo4j:Username"] ?? "neo4j";
    options.Password = builder.Configuration["Neo4j:Password"] ?? "password";
    options.EmbeddingDimensions = modelSettings.EmbeddingDimensions!.Value;
});
builder.Services.AddAgentMemoryCore(o => o.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IIdGenerator, GuidIdGenerator>();
builder.Services.AddSingleton(embeddingGenerator);
builder.Services.AddSingleton(chatClient);
// The model extracts entities, facts and preferences from each stored turn (the core's default extractors are stubs).
builder.Services.AddLlmExtraction(o => o.ApplyRecommended());
builder.Services.AddAgentMemoryFramework(options =>
{
    options.AutoExtractOnPersist = true;
    options.ContextFormat.IncludeEntities = true;
    options.ContextFormat.IncludeFacts = true;
    options.ContextFormat.IncludePreferences = true;
});

var host = builder.Build();
await using var hostDisposal = (IAsyncDisposable)host;
return await CopilotWithMemory.RunAsync(host.Services, modelSettings);

internal static class CopilotWithMemory
{
    internal const string UserId = "demo-user-copilot";
    internal const string ApplicationId = "agent-memory-dotnet-copilot";

    internal static async Task<int> RunAsync(IServiceProvider root, AgentMemory.Inference.InferenceProviderSettings model)
    {
        var logger = root.GetRequiredService<ILogger<Program>>();
        await using var scope = root.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        try { await sp.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(); }
        catch (Exception e) { logger.LogWarning("Schema bootstrap skipped (no live Neo4j): {Message}", e.Message); }

        var memory = new MemoryMessages(sp.GetRequiredService<Neo4jMemoryContextProvider>());
        var recall = sp.GetRequiredService<IMemoryContextAssembler>();
        var owners = sp.GetRequiredService<IWritableMemoryOwnerContext>();
        var inLoop = 0;
        var sessionId = "copilot-session-" + Guid.NewGuid().ToString("N")[..8];

        // BYOK: the Copilot runtime sends inference to the resolved provider, as an OpenAI-compatible chat-completions API.
        var config = new SessionConfig
        {
            Model = model.Model,
            Provider = new ProviderConfig { Type = "openai", WireApi = "chat-completions", BaseUrl = model.Endpoint, ApiKey = model.ApiKey, WireModel = model.Model },
            SystemMessage = new SystemMessageConfig { Content = "You are a friendly assistant with durable, graph-backed long-term memory. Answer briefly." },
            // A memory demo needs no tools: Copilot's file, shell and web tools are refused, never run on this machine.
#pragma warning disable GHCP001 // PermissionDecision is marked for evaluation in the SDK.
            OnPermissionRequest = (_, _) => Task.FromResult(GitHub.Copilot.Rpc.PermissionDecision.Reject("This sample runs no tools.")),
#pragma warning restore GHCP001
            Hooks = new SessionHooks
            {
                // Inside Copilot's loop: every prompt the session takes gets memory recalled for it. The hook runs on the
                // SDK's callback, outside the agent's async flow, so it opens the owner scope itself.
                OnUserPromptSubmitted = async (input, _) =>
                {
                    Interlocked.Increment(ref inLoop);
                    using var owner = owners.BeginOwnerScope(UserId);
                    var context = await recall.AssembleContextAsync(new RecallRequest { SessionId = sessionId, UserId = UserId, Query = input.Prompt });
                    return new UserPromptSubmittedHookOutput { AdditionalContext = Render(context) };
                },
            },
        };

        await using var copilot = new CopilotClient(new CopilotClientOptions
        {
            UseLoggedInUser = false,
            Connection = Environment.GetEnvironmentVariable("COPILOT_CLI_PATH") is { Length: > 0 } cli ? RuntimeConnection.ForStdio(cli) : null,
        });
        AIAgent agent = copilot.AsAIAgent(config)
            .AsBuilder().UseAIContextProviders(memory).Build()
            .WithMemoryOwnerScoping(sp);

        var session = (await agent.CreateSessionAsync()).WithMemoryIdentity(userId: UserId, sessionId: sessionId,
            conversationId: sessionId, applicationId: ApplicationId);
        if (!await TurnAsync(agent, session, "Hi, I'm Alex and I prefer aisle seats on flights.", logger)) return 1;
        if (!await TurnAsync(agent, session, "Which seat do I prefer?", logger)) return 1;

        // A new Copilot session has no history of its own: what it knows about Alex now comes from durable memory.
        var later = (await agent.CreateSessionAsync()).WithMemoryIdentity(userId: UserId, sessionId: sessionId + "-later",
            conversationId: sessionId + "-later", applicationId: ApplicationId);
        if (!await TurnAsync(agent, later, "I'm booking a flight. Which seat should I ask for?", logger)) return 1;

        Console.WriteLine();
        Console.WriteLine($"Memory around the outer turn: {memory.Recalls} recalls, {memory.Stores} stores (one each per RunAsync).");
        Console.WriteLine($"Memory inside Copilot's loop: {inLoop} prompt hooks.");
        return 0;
    }

    static async Task<bool> TurnAsync(AIAgent agent, AgentSession session, string turn, ILogger logger)
    {
        SampleConsole.WriteUser(turn);
        try
        {
            SampleConsole.WriteAssistant((await agent.RunAsync(turn, session)).Text);
            return true;
        }
        catch (Exception e)
        {
            logger.LogWarning("Turn failed: {Message}", e.Message);
            return false;
        }
    }

    // The recalled memory as plain lines: what the hook hands Copilot's loop.
    internal static string Render(MemoryContext context)
    {
        var lines = new List<string>();
        lines.AddRange(context.RelevantFacts.Items.Select(f => $"- {f.Subject} {f.Predicate} {f.Object}"));
        lines.AddRange(context.RelevantPreferences.Items.Select(p => $"- prefers ({p.Category}): {p.PreferenceText}"));
        lines.AddRange(context.RelevantEntities.Items.Select(e => $"- knows {e.Type}: {e.Name}"));
        return lines.Count == 0 ? "" : "What you remember about this user:\n" + string.Join("\n", lines);
    }
}

/// <summary>
/// Neo4jMemoryContextProvider for an agent that takes only messages: recall before each run (its recalled messages, placed
/// as the provider places them), persistence and extraction after it. Counts both.
/// </summary>
internal sealed class MemoryMessages(Neo4jMemoryContextProvider inner) : MessageAIContextProvider
{
    private int _recalls, _stores;

    public int Recalls => _recalls;

    public int Stores => _stores;

    protected override async ValueTask<IEnumerable<ChatMessage>> ProvideMessagesAsync(InvokingContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _recalls);
        var request = context.RequestMessages.ToList();
#pragma warning disable MAAI001 // The context constructor is under evaluation; MAF builds the same object the same way.
        var merged = await inner.InvokingAsync(new AIContextProvider.InvokingContext(context.Agent, context.Session,
            new AIContext { Messages = request }), cancellationToken).ConfigureAwait(false);
#pragma warning restore MAAI001
        return (merged.Messages ?? []).Where(m => !request.Any(r => ReferenceEquals(r, m))).ToList();
    }

    protected override ValueTask StoreAIContextAsync(InvokedContext context, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _stores);
        return inner.InvokedAsync(context, cancellationToken);
    }
}
