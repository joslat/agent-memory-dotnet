using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Examples.Commitments;
using AgentMemory.Extensibility.AgentFramework;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Contributors;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Commitments.NativeChecks;

internal sealed class WorldClock : TimeProvider, IClock
{
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

// This is the terminal chat client, not a memory/store double. Its reply never answers from memory.
// All assertions inspect the exact inputs that a real coding/chat model would have received.
internal sealed class RecordingChatClient(string responseId, string responseText) : IChatClient
{
    public List<ChatInput> Inputs { get; } = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Inputs.Add(new(options?.Instructions, messages.Select(m => new MessageInput(m.Role.Value, m.MessageId, m.Text)).ToArray()));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, responseText) { MessageId = responseId })
            { ResponseId = responseId, FinishReason = ChatFinishReason.Stop });
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in (await GetResponseAsync(messages, options, cancellationToken)).ToChatResponseUpdates())
            yield return update;
    }
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;
    public void Dispose() { }
}
internal sealed record MessageInput(string Role, string? Id, string Text);
internal sealed record ChatInput(string? Instructions, IReadOnlyList<MessageInput> Messages)
{
    public string AllText => (Instructions ?? "") + "\n" + string.Join("\n", Messages.Select(m => m.Text));
}
internal sealed record CapturedIngestion(IngestionRequest Request, IngestionReport Report);
internal sealed record NativeRun(IReadOnlyList<ChatInput> Inputs, IReadOnlyList<ContextEnvelope> Envelopes,
    IReadOnlyList<CapturedIngestion> Ingestion);

internal sealed class CapturingIngestion(IModuleIngestion inner, List<CapturedIngestion> captured) : IModuleIngestion
{
    public async Task<IngestionReport> IngestAsync(IngestionRequest request, CancellationToken cancellationToken = default)
    {
        var report = await inner.IngestAsync(request, cancellationToken);
        captured.Add(new(request, report));
        return report;
    }
    public async Task<IngestionReport> ReplayAsync(IngestionRequest request, IReadOnlyList<RecordedProposals> recorded,
        CancellationToken cancellationToken = default)
    {
        var report = await inner.ReplayAsync(request, recorded, cancellationToken);
        captured.Add(new(request, report));
        return report;
    }
}
internal sealed class CapturingCompiler(IContextCompiler inner, List<ContextEnvelope> captured) : IContextCompiler
{
    public async Task<ContextEnvelope> CompileAsync(ContextRequest request, IReadOnlySet<string>? excludedContributorIds = null,
        CancellationToken cancellationToken = default)
    {
        var envelope = await inner.CompileAsync(request, excludedContributorIds, cancellationToken);
        captured.Add(envelope);
        return envelope;
    }
}

internal sealed class NativeHost : IAsyncDisposable
{
    public ServiceProvider Services { get; }
    private readonly ModuleResolution? _resolution;
    public ModuleResolution Resolution => _resolution ?? throw new InvalidOperationException("This host has no modules.");
    public WorldClock Clock { get; }
    public List<CapturedIngestion> Captured { get; } = [];
    public List<ContextEnvelope> Envelopes { get; } = [];
    public JsonElement? Selection { get; set; }

    public NativeHost(string uri, string password, WorldClock clock, bool modules)
    {
        Clock = clock;
        if (modules)
        {
            _resolution = ModuleResolver.Resolve([CommitmentsModule.Manifest], ModuleVersion.Parse("0.1.0"),
                new HashSet<string>(StringComparer.Ordinal) { "ModuleSchema" });
            if (!Resolution.Succeeded) throw new InvalidOperationException("Package manifest resolution failed.");
        }
        var options = new MemoryOptions
        {
            Recall = RecallOptions.Default with { MaxRecentMessages = 0, MaxRelevantMessages = 0 }
        };
        options.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(options);
        services.AddNeo4jAgentMemory(o =>
        {
            o.Uri = uri; o.Username = "neo4j"; o.Password = password; o.Database = "neo4j"; o.EmbeddingDimensions = 4;
            o.ConnectionAcquisitionTimeout = TimeSpan.FromSeconds(15);
            o.TransactionTimeout = TimeSpan.FromSeconds(30);
        });
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), 4));
        services.AddAgentMemoryFramework(o => { o.AutoExtractOnPersist = false; o.ExtractInBackground = false; });
        if (modules)
        {
            services.AddAgentMemoryFrameworkModuleIngestion(sp => Pipeline(sp));
            services.AddScoped<IContextContributor>(sp =>
            {
                var reader = new Neo4jModuleReader(sp.GetRequiredService<INeo4jTransactionRunner>(), Resolution,
                    CommitmentsModule.Id, sp.GetRequiredService<IMemoryIsolationPolicy>(), clock);
                var selection = Selection;
                var view = selection?.GetProperty("view").GetString();
                return view switch
                {
                    "due" => CommitmentsModule.Due(Resolution, reader),
                    "history" => new ViewContributor(Resolution, reader, "history", 100, _ => new ModuleViewArguments
                    {
                        Key = selection!.Value.GetProperty("key").GetString(),
                        Limit = selection.Value.TryGetProperty("limit", out var limit) ? limit.GetInt32() : null
                    }),
                    _ => CommitmentsModule.ForEntity(Resolution, reader, _ => selection is { } value
                        && value.TryGetProperty("aboutEntityId", out var target) ? target.GetString() : null)
                };
            });
            services.AddScoped<IContextCompiler>(sp => new CapturingCompiler(new ContextCompiler(
                sp.GetServices<IContextContributor>(), sp.GetRequiredService<IMemoryIsolationPolicy>(), clock,
                sp.GetRequiredService<ILogger<ContextCompiler>>()), Envelopes));
        }
        Services = services.BuildServiceProvider(validateScopes: true);
        var effective = Services.GetRequiredService<IOptions<MemoryOptions>>().Value;
        if (effective.Recall.MaxRecentMessages != 0 || effective.Recall.MaxRelevantMessages != 0)
            throw new InvalidOperationException("Teaching-history recall must be disabled in every host.");
    }

    private IModuleIngestion Pipeline(IServiceProvider sp) => new CapturingIngestion(new ModuleIngestion(Resolution,
        [new CommitmentProvider()], new Dictionary<string, IModuleStore>
        {
            [CommitmentsModule.Id] = new Neo4jModuleStore(sp.GetRequiredService<INeo4jTransactionRunner>(), Resolution)
        }, sp.GetRequiredService<IMemoryIsolationPolicy>(), Clock, sp.GetRequiredService<ILogger<ModuleIngestion>>()), Captured);

    public async Task<IngestionReport> ReplayAsync(CapturedIngestion captured)
    {
        using var scope = Services.CreateScope();
        return await Pipeline(scope.ServiceProvider).ReplayAsync(captured.Request, captured.Report.Recorded);
    }

    public async Task<NativeRun> RunAsync(string owner, string sessionId, string conversationId,
        string text, string userMessageId, string assistantMessageId, DateTimeOffset? systemAsOf = null,
        DateTimeOffset? validAsOf = null, string assistantText = "Acknowledged.")
    {
        var beforeIngestion = Captured.Count; var beforeEnvelopes = Envelopes.Count;
        using var scope = Services.CreateScope(); var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IShortTermMemoryService>().AddConversationAsync(conversationId, sessionId, owner);
        using var client = new RecordingChatClient(assistantMessageId, assistantText);
        var agent = client.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "CommitmentsAcceptance",
            ChatOptions = new() { Instructions = "Acknowledge the request." },
            AIContextProviders = [sp.GetRequiredService<Neo4jMemoryContextProvider>()],
            ChatHistoryProvider = AgentMemoryChatHistory.CreateInMemoryProvider()
        }).WithMemoryOwnerScoping(sp);
        // Actual native agent loop. Every invocation gets a new in-memory session, including reads.
        var session = (await agent.CreateSessionAsync()).WithMemoryIdentity(owner, sessionId, conversationId);
        session.WithModuleReadTime(systemAsOf, validAsOf);
        await agent.RunAsync([new ChatMessage(ChatRole.User, text) { MessageId = userMessageId }], session);
        return new(client.Inputs.ToArray(), Envelopes.Skip(beforeEnvelopes).ToArray(), Captured.Skip(beforeIngestion).ToArray());
    }
    public ValueTask DisposeAsync() => Services.DisposeAsync();
}
