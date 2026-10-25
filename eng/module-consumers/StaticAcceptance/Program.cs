using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Examples.Commitments;
using AgentMemory.Examples.RepositoryLessons;
using AgentMemory.Extensibility;
using AgentMemory.Extensibility.AgentFramework;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Gate;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Validation;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;

// EXT-static-acceptance: both validation modules on static composition, end to end, on a disposable Neo4j, from NuGet
// packages only. Part A runs each module's pack through the conformance kit; part B composes a Microsoft Agent Framework
// host with both modules and drives turns through the real agent loop. Deterministic: scripted lesson writer, a keyword
// gate and an approving update judge stand in for the models; nothing here grades a model.
var output = args.SkipWhile(a => a != "--output").Skip(1).FirstOrDefault() ?? throw new ArgumentException("--output is required");
Directory.CreateDirectory(output);
var uri = Environment.GetEnvironmentVariable("NEO4J_URI") ?? "";
var password = Environment.GetEnvironmentVariable("NEO4J_PASSWORD") ?? "";
if (Environment.GetEnvironmentVariable("AGENTMEMORY_DISPOSABLE_NEO4J") != "1" || !Uri.TryCreate(uri, UriKind.Absolute, out var bolt)
    || bolt.Scheme != "bolt" || !(bolt.IsLoopback) || password.Length == 0)
    throw new InvalidOperationException("An explicit disposable loopback Bolt service is required.");

var checks = new List<object>(); var failed = 0;
void Require(string name, bool passed, string detail)
{
    if (!passed) failed++;
    checks.Add(new { name, passed, detail });
    Console.WriteLine($"{(passed ? "pass" : "FAIL")} {name}: {detail}");
}
void Neo4j(Neo4jOptions o)
{
    o.Uri = uri; o.Username = "neo4j"; o.Password = password; o.Database = "neo4j"; o.EmbeddingDimensions = 4;
}

// ---- Part A: each module's pack through the conformance kit ----
var packs = new Dictionary<string, string>();
foreach (var file in new[] { "commitments.json", "lessons.json" })
{
    var path = Path.Combine(AppContext.BaseDirectory, "Packs", file);
    packs[file] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    var pack = ValidationPackReader.ReadFile(path);
    var problems = ValidationPackReader.Check(pack);
    Require($"pack:{pack.Id}:well-formed", problems.Count == 0, problems.Count == 0 ? "no problems" : string.Join("; ", problems));
    var runner = new ValidationPackRunner(Neo4j)
    {
        Modules = modules => modules
            .GrantChatClient(_ => new ScriptedLessons())
            .Add<RepositoryLessonsModule>()
            .Add(CommitmentsModule.Manifest, _ => { }),
    };
    var result = await runner.RunAsync(pack);
    foreach (var check in result.Checks) Require($"pack:{pack.Id}:{check.Id}", check.Passed, check.Detail);
}
// A mutation control on this host: a pack expecting a record the pipeline never wrote must fail.
{
    var pack = ValidationPackReader.ReadFile(Path.Combine(AppContext.BaseDirectory, "Packs", "lessons.json"));
    var broken = pack with { Modules = pack.Modules! with { Teach = [pack.Modules!.Teach[0], pack.Modules.Teach[2]] } };
    var result = await new ValidationPackRunner(Neo4j)
    {
        Modules = m => m.GrantChatClient(_ => new ScriptedLessons()).Add<RepositoryLessonsModule>().Add(CommitmentsModule.Manifest, _ => { }),
    }.RunAsync(broken);
    Require("pack:mutation-control", result.Failures.Any(f => f.Id == "records:dev-new"),
        "a missing teach step fails the record check that names it: " + string.Join(", ", result.Failures.Select(f => f.Id)));
}

// ---- Part B: a composed Agent Framework host with both modules ----
var gate = new KeywordGate(["Atlas", "alt-build", "build2"]);
var lessonsModel = new ScriptedLessons();
await using (var modulesHost = await HostAsync(modules: true, maxItems: 8))
{
    await modulesHost.EntityAsync("atlas-dev", "Atlas", "dev");
    const string teach = """{"format":"agentmemory.commitment/v1","key":"atlas-draft","title":"Send Atlas draft","detail":"Promised to the board","status":"open","priority":2,"due_at":"2026-10-23T16:00:00Z","about":{"kind":"name","target":"Atlas"}}""";
    await modulesHost.TurnAsync("dev", "s1", teach);
    await modulesHost.TurnAsync("dev", "s2", "Builds fail with MSB3021 because a running process locks the DLLs; building into artifacts/alt-build fixed it and the build passed.");
    Require("host:writes:commitment", await modulesHost.CountAsync("CommitmentItem", "dev") == 1, "the teach turn stored one commitment through the framework");
    Require("host:writes:lesson", await modulesHost.CountAsync("LessonItem", "dev") == 1, "the coding turn stored one lesson through the model-backed writer");

    modulesHost.Clock.UtcNow = DateTimeOffset.Parse("2026-10-24T09:00:00Z");
    var calls = gate.Requests.Count;
    var recall = await modulesHost.TurnAsync("dev", "s3", "What is due, and how do we get past locked DLLs?");
    Require("host:recall:commitment", recall.Contains("Send Atlas draft", StringComparison.Ordinal), "the due commitment reached the model in a fresh session");
    Require("host:recall:lesson", recall.Contains("alt-build", StringComparison.Ordinal), "the lesson reached the model in a fresh session");
    var pooled = gate.Requests.Skip(calls).ToList();
    Require("host:pool:one-call", pooled.Count == 1, $"{pooled.Count} gate call(s) for the recall: core and module memories judged together");
    Require("host:pool:companions", pooled.SelectMany(r => r.Candidates).Count(c => c.Companion) == 2
        && pooled.SelectMany(r => r.Candidates).Where(c => c.Companion).All(c => c.MemoryType.StartsWith("io.agentmemory.examples.", StringComparison.Ordinal)),
        "both module items were companions under their module's type");

    var other = await modulesHost.TurnAsync("sam", "s4", "What is due, and how do we get past locked DLLs?");
    Require("host:isolation", !other.Contains("Send Atlas draft", StringComparison.Ordinal) && !other.Contains("alt-build", StringComparison.Ordinal),
        "another owner's fresh session received none of dev's module memories");

    await modulesHost.TurnAsync("dev", "s5", "Use --artifacts-path artifacts/build2 instead; alt-build collides with the CI cache.");
    var after = await modulesHost.TurnAsync("dev", "s6", "How do we get past locked DLLs?");
    Require("host:closing:new-lesson", after.Contains("build2", StringComparison.Ordinal), "the replacing lesson is recalled");
    Require("host:closing:old-lesson-closed", !after.Contains("into artifacts/alt-build fixed", StringComparison.Ordinal)
        && await modulesHost.ClosedAsync("LessonItem", "dev", "locked-dlls"),
        "the replaced lesson was closed through the host's closing approver (kept as history, not recalled)");
}
await using (var budgetHost = await HostAsync(modules: true, maxItems: 1))
{
    await budgetHost.EntityAsync("atlas-bud", "Atlas", "bud");
    await budgetHost.TurnAsync("bud", "b1", """{"format":"agentmemory.commitment/v1","key":"atlas-draft","title":"Send Atlas draft","status":"open","priority":2,"due_at":"2026-10-23T16:00:00Z","about":{"kind":"name","target":"Atlas"}}""");
    await budgetHost.TurnAsync("bud", "b2", "Builds fail with MSB3021 because a running process locks the DLLs; building into artifacts/alt-build fixed it and the build passed.");
    budgetHost.Clock.UtcNow = DateTimeOffset.Parse("2026-10-24T09:00:00Z");
    var limited = await budgetHost.TurnAsync("bud", "b3", "What is due, and how do we get past locked DLLs?");
    var rendered = new[] { "Send Atlas draft", "alt-build" }.Count(t => limited.Contains(t, StringComparison.Ordinal));
    Require("host:budget", rendered == 1, $"with MaxItems 1, {rendered} module item(s) reached the model");
}
string withModules, withoutModules;
await using (var parityModules = await HostAsync(modules: true, maxItems: 8))
    withModules = await parityModules.TurnAsync("zoe", "p1", "What is due this week?");
var moduleCalls = lessonsModel.Calls;
await using (var parityCore = await HostAsync(modules: false, maxItems: 8))
    withoutModules = await parityCore.TurnAsync("zoe-core", "p1-core", "What is due this week?");
Require("host:parity", withModules == withoutModules, "with modules composed and nothing of theirs to say, the model input is byte-identical to a host without modules");
Require("host:neutrality", lessonsModel.Calls == moduleCalls, "a host without modules runs no module code");

File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new
{
    format = "ext-static-acceptance/1", packs, passed = checks.Count - failed, failed, checks,
    sdk = typeof(ModuleSet).Assembly.Location, commitments = typeof(CommitmentsModule).Assembly.Location,
    lessons = typeof(RepositoryLessonsModule).Assembly.Location,
}, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"ext-static-acceptance: {checks.Count - failed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

async Task<AcceptanceHost> HostAsync(bool modules, int maxItems)
{
    var clock = new AcceptanceClock { UtcNow = DateTimeOffset.Parse("2026-10-20T09:00:00Z") };
    var options = new MemoryOptions { Recall = RecallOptions.Default with { MaxRecentMessages = 0, MaxRelevantMessages = 0 } };
    options.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant;
    var services = new ServiceCollection();
    services.AddLogging();
    services.AddAgentMemoryCore(options);
    services.AddNeo4jAgentMemory(Neo4j);
    services.AddSingleton<IClock>(clock);
    services.AddSingleton<TimeProvider>(clock);
    services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp => new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), 4));
    // The deterministic judges, registered before the gate so its TryAdd defaults keep them.
    services.AddSingleton<IMemoryGate>(gate);
    services.AddSingleton<IMemoryUpdateJudge>(new ApprovingUpdateJudge());
    services.AddAgentMemoryGate(o => o.Mode = MemoryGateMode.Judge);
    services.AddAgentMemoryFramework(o => { o.AutoExtractOnPersist = false; o.ExtractInBackground = false; });
    if (modules)
    {
        services.AddAgentMemoryModules(m => m
            .UseNeo4jStore()
            .GrantChatClient(_ => lessonsModel)
            .Add<RepositoryLessonsModule>()
            .Add(CommitmentsModule.Manifest, b =>
            {
                b.AddIngestionProvider(_ => new CommitmentProvider());
                b.AddContextContributor(c => CommitmentsModule.Due(c.Resolution, c.Reader));
            }));
        services.AddAgentMemoryFrameworkExtensibility();
        services.AddAgentMemoryFrameworkModuleIngestion();
        services.AddAgentMemoryFrameworkModuleSelection(new ModuleSelectionOptions { UseJudge = true, MaxItems = maxItems });
    }
    var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    var runner = provider.GetRequiredService<INeo4jTransactionRunner>();
    await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();
    if (modules) await new ModuleMigrator(runner, provider.GetRequiredService<ModuleSet>().Resolution, clock).ApplyAsync("static-acceptance");
    return new AcceptanceHost(provider, clock);
}

sealed class AcceptanceClock : TimeProvider, IClock
{
    public DateTimeOffset UtcNow { get; set; }
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

sealed class AcceptanceHost(ServiceProvider services, AcceptanceClock clock) : IAsyncDisposable
{
    private int _messages;
    public AcceptanceClock Clock { get; } = clock;

    public Task EntityAsync(string id, string name, string owner) => services.GetRequiredService<INeo4jTransactionRunner>().WriteAsync(async tx =>
        await (await tx.RunAsync("CREATE (:Entity {id:$id,name:$name,owner_id:$owner,created_at:datetime($at)})",
            new { id, name, owner, at = Clock.UtcNow.ToString("O") })).ConsumeAsync());

    /// <summary>One turn through the real agent loop in a fresh session; returns everything the model was given.</summary>
    public async Task<string> TurnAsync(string owner, string session, string text)
    {
        Clock.UtcNow = Clock.UtcNow.AddMinutes(1);
        using var scope = services.CreateScope();
        var sp = scope.ServiceProvider;
        await sp.GetRequiredService<IShortTermMemoryService>().AddConversationAsync(session, session, owner);
        using var model = new RecordingModel();
        var agent = model.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "StaticAcceptance", ChatOptions = new() { Instructions = "Answer briefly." },
            AIContextProviders = [sp.GetRequiredService<Neo4jMemoryContextProvider>()],
            ChatHistoryProvider = AgentMemoryChatHistory.CreateInMemoryProvider(),
        }).WithMemoryOwnerScoping(sp);
        var agentSession = (await agent.CreateSessionAsync()).WithMemoryIdentity(owner, session, session);
        await agent.RunAsync([new ChatMessage(ChatRole.User, text) { MessageId = $"m{++_messages}-{session}" }], agentSession);
        return model.Seen;
    }

    public Task<int> CountAsync(string label, string owner) => services.GetRequiredService<INeo4jTransactionRunner>().ReadAsync(async tx =>
        (await (await tx.RunAsync($"MATCH (n:{label} {{owner_id:$owner}}) WHERE n.invalidated_at IS NULL RETURN count(n) AS n", new { owner })).SingleAsync())["n"].As<int>());

    public Task<bool> ClosedAsync(string label, string owner, string key) => services.GetRequiredService<INeo4jTransactionRunner>().ReadAsync(async tx =>
        (await (await tx.RunAsync($"MATCH (n:{label} {{owner_id:$owner, key:$key}}) RETURN count(n) > 0 AND all(x IN collect(n) WHERE x.invalidated_at IS NOT NULL) AS closed",
            new { owner, key })).SingleAsync())["closed"].As<bool>());

    public ValueTask DisposeAsync() => services.DisposeAsync();
}

/// <summary>The terminal model: it answers nothing from memory and records exactly what it was given.</summary>
sealed class RecordingModel : IChatClient
{
    public string Seen { get; private set; } = "";
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Seen = (options?.Instructions ?? "") + "\n" + string.Join("\n", messages.Select(m => m.Role + ": " + m.Text));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Noted.")));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>The lessons model: a fixed lesson for the coding turns, and nothing for any other turn.</summary>
sealed class ScriptedLessons : IChatClient
{
    public int Calls { get; private set; }
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Calls++;
        var said = messages.Last().Text;
        var reply = said.Contains("artifacts/build2", StringComparison.Ordinal)
            ? """{"lessons":[{"key":"locked-dlls-ci","lesson":"When MSB3021 locks the DLLs, build into artifacts/build2; alt-build collides with the CI cache","fix":"dotnet build --artifacts-path artifacts/build2","replaces":["locked-dlls"]}]}"""
            : said.Contains("MSB3021", StringComparison.Ordinal)
                ? """{"lessons":[{"key":"locked-dlls","lesson":"When MSB3021 says a process locks the DLLs, build into artifacts/alt-build fixed it","failure":"MSB3021 Unable to copy file","fix":"dotnet build --artifacts-path artifacts/alt-build","verified":true}]}"""
                : """{"lessons":[]}""";
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
    }
    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) => throw new NotSupportedException();
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}

/// <summary>The retrieval judge: memories naming a keyword are needed, the rest are not; every request is recorded.</summary>
sealed class KeywordGate(IReadOnlyList<string> keywords) : IMemoryGate
{
    public List<MemoryGateRequest> Requests { get; } = [];
    public Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default)
    {
        lock (Requests) Requests.Add(request);
        return Task.FromResult(new MemoryGateDecision(request.Candidates.ToDictionary(c => c.Key,
            c => keywords.Any(k => c.Text.Contains(k, StringComparison.Ordinal)) ? 0.9 : 0.05), "keyword"));
    }
}

/// <summary>The update judge: every named replacement is a replacement (the host's judge, for the closing approver).</summary>
sealed class ApprovingUpdateJudge : IMemoryUpdateJudge
{
    public bool IsEnabled => true;
    public Task<IReadOnlyDictionary<string, double>> JudgeAsync(MemoryUpdateRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, double>>(request.Pairs.ToDictionary(p => p.Key, _ => 0.9));
}
