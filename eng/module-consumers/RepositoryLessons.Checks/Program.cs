using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Examples.RepositoryLessons;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

var results = new List<object>(); var failed = 0;
var manifest = ModuleManifest.LoadFromAssemblyFile(typeof(RepositoryLessonsModule).Assembly.Location);
var resolution = ModuleResolver.Resolve([manifest], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema", "Inference" });
void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Check(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.GetType().Name + ": " + e.Message }); }
}

ModuleViewItem Kept(string key, string lesson) => new()
{
    Text = lesson + " (agent-memory-dotnet)",
    Record = new()
    {
        ModuleId = RepositoryLessonsModule.Id, Type = RepositoryLessonsModule.Type, Id = "v-" + key, Key = key, OwnerId = "dev",
        CreatedAtUtc = DateTimeOffset.UnixEpoch, SourceSessionId = "earlier", Source = new() { MessageIds = ["m0"], Excerpt = "earlier" },
        Properties = new Dictionary<string, JsonElement>
        {
            ["lesson"] = JsonSerializer.SerializeToElement(lesson), ["repository"] = JsonSerializer.SerializeToElement("agent-memory-dotnet"),
            ["verified"] = JsonSerializer.SerializeToElement(false),
        },
    },
};
IngestionRequest Turn(params ModuleViewItem[] kept) => new()
{
    SessionId = "session", Owner = "dev", AtUtc = DateTimeOffset.Parse("2026-10-25T10:00:00Z"),
    Messages =
    [
        new() { Id = "m1", Role = "user", Text = "The integration tests fail with 'Unable to copy file ... being used by another process'." },
        new() { Id = "m2", Role = "assistant", Text = "A running asker holds the DLLs. Building into a separate artifacts path fixed it; the build passed." },
    ],
    Existing = kept,
    CoreOutcome = new() { Stored = [new() { Id = "fact-1", Kind = "Fact", Text = "dev works on agent-memory-dotnet" }] },
};
const string Reply = """
    {"lessons": [
      {"key": "build-into-artifacts-path", "lesson": "When a running process locks bin DLLs (MSB3021), build with --artifacts-path elsewhere.",
       "failure": "MSB3021 Unable to copy file ... being used by another process", "fix": "dotnet build --artifacts-path artifacts/alt-build",
       "verified": true, "replaces": ["kill-the-build-server", "not-shown"]},
      {"key": "Not A Key", "lesson": "dropped"},
      {"key": "no-text"}
    ]}
    """;

await Check("embedded-package-metadata", () =>
{
    Require(resolution.Succeeded, string.Join(";", resolution.Errors));
    Require(manifest.Id == RepositoryLessonsModule.Id && manifest.Host.SequenceEqual(new[] { "Inference" }), "Wrong identity or host features");
    Require(manifest.Views.Single().Id == RepositoryLessonsModule.CurrentView, "Current view missing");
    return Task.CompletedTask;
});
await Check("writer-sees-kept-lessons-core-write-and-conversation", () =>
{
    var prompt = LessonWriter.Prompt(Turn(Kept("kill-the-build-server", "Kill the build server when DLLs are locked.")), "agent-memory-dotnet");
    var said = prompt[1].Text;
    Require(prompt[0].Text == LessonWriter.Instructions, "Instructions are not the module's policy");
    Require(said.Contains("kill-the-build-server: Kill the build server", StringComparison.Ordinal), "Kept lesson missing");
    Require(said.Contains("dev works on agent-memory-dotnet", StringComparison.Ordinal), "Core write missing");
    Require(said.Contains("assistant: A running asker", StringComparison.Ordinal), "Conversation missing");
    return Task.CompletedTask;
});
await Check("reply-becomes-admitted-proposals", () =>
{
    var request = Turn(Kept("kill-the-build-server", "Kill the build server when DLLs are locked."));
    var proposals = LessonWriter.Parse(Reply, request, new RepositoryLessonsOptions { Repository = "agent-memory-dotnet" });
    var lesson = proposals.Single();
    Require(lesson.Key == "build-into-artifacts-path" && lesson.Type == RepositoryLessonsModule.Type, "Wrong record identity");
    Require(lesson.Replaces.SequenceEqual(new[] { "kill-the-build-server" }), "Replaces must keep only shown lessons");
    Require(lesson.Properties["verified"].GetBoolean() && lesson.Properties["repository"].GetString() == "agent-memory-dotnet", "Lost values");
    Require(lesson.Source.MessageIds.SequenceEqual(new[] { "m1", "m2" }), "Fabricated source");
    var writer = new LessonWriter(new ScriptedChat(Reply));
    Require(ModuleProposalAdmission.Admit(resolution, writer.Descriptor, request, lesson).Accepted, "SDK refused a valid lesson");
    return Task.CompletedTask;
});
await Check("a-reply-that-is-not-lessons-is-an-error", () =>
{
    foreach (var bad in new[] { "no json here", "{\"notes\": []}", "{\"lessons\": " })
    {
        try { LessonWriter.Parse(bad, Turn(), new RepositoryLessonsOptions()); throw new InvalidOperationException("Accepted: " + bad); }
        catch (FormatException) { }
    }
    Require(LessonWriter.Parse("{\"lessons\": []}", Turn(), new RepositoryLessonsOptions()).Count == 0, "Empty answer must store nothing");
    return Task.CompletedTask;
});
await Check("static-composition-writes-and-closes-through-the-host", async () =>
{
    var store = new MemoryStore();
    var chat = new ScriptedChat(Reply);
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IMemoryIsolationPolicy>(new OwnerPolicy());
    services.AddSingleton<IMemoryClosingApprover>(new ApproveAll());
    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["AgentMemory:Modules:io.agentmemory.examples.lessons:Repository"] = "agent-memory-dotnet" }).Build());
    services.AddAgentMemoryModules(m => m.UseStore(new ProbeStoreAdapter(store, Kept("kill-the-build-server", "Kill the build server.")))
        .GrantChatClient(_ => chat).Add<RepositoryLessonsModule>());
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    Require(provider.GetRequiredService<ModuleSet>().Modules.Single().GetOptions<RepositoryLessonsOptions>().Repository == "agent-memory-dotnet", "Options not bound");
    await using var scope = provider.CreateAsyncScope();
    var report = await scope.ServiceProvider.GetRequiredService<IModuleIngestion>().IngestAsync(Turn() with { Existing = [] });
    var outcome = report.Providers.Single().Proposals.Single();
    Require(outcome.Status == ProposalWriteStatus.Stored && outcome.Closed.SequenceEqual(new[] { "kill-the-build-server" }), "Replacement did not close");
    Require(store.Batches.Single().Proposals.Select(p => p.Proposal.Kind).SequenceEqual(new[] { ProposalKind.Assert, ProposalKind.Invalidate }), "Not one write");
    Require(chat.Calls == 1, "One model call a turn");
});
await Check("no-grant-no-activation", async () =>
{
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddSingleton<IMemoryIsolationPolicy>(new OwnerPolicy());
    services.AddAgentMemoryModules(m => m.UseStore(new ProbeStoreAdapter(new MemoryStore())).Add<RepositoryLessonsModule>());
    await using var provider = services.BuildServiceProvider();
    try { provider.GetRequiredService<ModuleSet>(); throw new InvalidOperationException("Activated without a granted model"); }
    catch (ModuleActivationException e) { Require(e.Errors.Any(x => x.Contains("No chat model", StringComparison.Ordinal)), "Wrong refusal"); }
});

Console.WriteLine(JsonSerializer.Serialize(new { packageVersion = manifest.Version.Value, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    assembly = typeof(RepositoryLessonsModule).Assembly.Location, sdk = typeof(ModuleManifest).Assembly.Location, passed = results.Count - failed, failed, checks = results }));
return failed == 0 ? 0 : 1;

sealed class ScriptedChat(string reply) : IChatClient
{
    public int Calls { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    { Calls++; return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply))); }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

sealed class OwnerPolicy : IMemoryIsolationPolicy
{
    public MemoryScope ResolveReadScope(MemoryScope? explicitScope, string? ownerId, string operationName, MemoryOperationAccess access) =>
        explicitScope ?? MemoryScope.For(ownerId ?? "dev");
    public string? ResolveWriteOwner(string? ownerId, string operationName, MemoryOperationAccess access) => ownerId;
}

sealed class ApproveAll : IMemoryClosingApprover
{
    public bool CanApprove(bool named) => true;
    public Task<IReadOnlyDictionary<string, MemoryClosingVerdict>> ApproveAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, MemoryClosingVerdict>>(request.Pairs.ToDictionary(p => p.Key, _ => new MemoryClosingVerdict(0.9, true)));
}

sealed class MemoryStore : IModuleStore
{
    public List<ModuleWriteBatch> Batches { get; } = [];
    public Task<IReadOnlyList<ProposalOutcome>> WriteAsync(ModuleWriteBatch batch, CancellationToken cancellationToken = default)
    {
        Batches.Add(batch);
        return Task.FromResult<IReadOnlyList<ProposalOutcome>>([.. batch.Proposals.Select(p => new ProposalOutcome
        {
            Index = p.Index, RecordId = "r-" + p.Proposal.Key,
            Status = p.Proposal.Kind == ProposalKind.Invalidate ? ProposalWriteStatus.Invalidated : ProposalWriteStatus.Stored,
        })]);
    }
}

/// <summary>A test double store adapter: an in-memory store and a reader over fixed records.</summary>
sealed class ProbeStoreAdapter(MemoryStore store, params ModuleViewItem[] kept) : IModuleStoreAdapter
{
    public string Provider => "probe";
    public IReadOnlyList<string> GetProblems(IServiceProvider services, string? serviceKey = null) => [];
    public IModuleStore CreateStore(IServiceProvider services, ModuleResolution resolution) => store;
    public IModuleReader CreateReader(IServiceProvider services, ModuleResolution resolution, string moduleId) => new ProbeReader(moduleId, kept);
}

sealed class ProbeReader(string moduleId, IReadOnlyList<ModuleViewItem> kept) : IModuleReader
{
    public string CallerModuleId => moduleId;
    public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded, Records = [.. kept.Select(k => k.Record)] });
}
