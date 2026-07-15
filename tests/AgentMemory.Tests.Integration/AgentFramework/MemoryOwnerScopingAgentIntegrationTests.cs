using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Tools;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;

// InvokingContext/InvokedContext are marked [Experimental] ("evaluation purposes only") by MAF as of
// v1.9.0 -- constructing them directly (rather than via the framework's own invocation pipeline) is
// exactly what this test needs to drive Neo4jMemoryContextProvider's real public entry points without a
// full IChatClient/tool-calling loop.
#pragma warning disable MAAI001

namespace AgentMemory.Tests.Integration.AgentFramework;

/// <summary>
/// Live-Neo4j proof of #90: a real, DI-resolved <see cref="Neo4jMemoryContextProvider"/> and
/// <see cref="MemoryToolFactory"/>, invoked through <see cref="MemoryOwnerScopingAgent"/>, observe the same
/// owner across passive recall, a model-invoked tool call, and automatic extraction/persistence -- within
/// one invocation, and without cross-invocation leakage under real concurrency. Complements
/// <c>MemoryOwnerScopingAgentTests</c> (unit-level, synthetic inner agent) by exercising the real provider
/// and tool wiring against real Neo4j.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class MemoryOwnerScopingAgentIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    public MemoryOwnerScopingAgentIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();

        // Deterministic preference extractor registered BEFORE AddNeo4jAgentMemory so it wins the
        // TryAddScoped override -- same convention as ExtractionOwnerStampIntegrationTests.cs.
        services.AddSingleton<IPreferenceExtractor, DeterministicPreferenceExtractor>();

        services.AddNeo4jAgentMemory(
            configureMemory: _ => { },
            configureNeo4j: o =>
            {
                o.Uri = _fixture.ConnectionString;
                o.Username = _fixture.User;
                o.Password = _fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });
        services.AddAgentMemoryFramework(o => o.AutoExtractOnPersist = true);
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(
                sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(),
                Neo4jIntegrationFixture.TestEmbeddingDimensions));

        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Invocation_ScopesToolCallAndPersistenceExtractionToSameOwner()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var ownerContext = sp.GetRequiredService<IWritableMemoryOwnerContext>();
        var scriptedAgent = new ScriptedInnerAgent(
            sp.GetRequiredService<Neo4jMemoryContextProvider>(),
            sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions(),
            responseText: "Noted that you prefer dark mode.")
            .WithMemoryOwnerScoping(ownerContext);

        var session = new TestAgentSession().WithMemoryIdentity(
            userId: "alice", sessionId: "sess-1", conversationId: "conv-1");

        await scriptedAgent.RunAsync("Remember that I prefer dark mode.", session);

        // The tool call (remember_fact, simulating a model-invoked tool) must be owner-stamped alice --
        // this is the exact mechanism #90 fixes: without MemoryOwnerScopingAgent, the ambient owner set by
        // the provider's pre-run hook does not survive into a tool call running after that hook returns.
        var facts = sp.GetRequiredService<IFactRepository>();
        var fact = await facts.FindByTripleAsync("Ruaidhri", "likes", "tea", scope: MemoryScope.For("alice"));
        fact.Should().NotBeNull("the simulated tool call ran inside the owner scope established for the whole invocation");
        fact!.OwnerId.Should().Be("alice");

        // Automatic persistence + extraction (InvokedAsync -> PerformStoreAsync) must ALSO stamp alice --
        // proving recall/tool/persistence all observe the same owner within one invocation.
        var preferences = sp.GetRequiredService<IPreferenceRepository>();
        var alicePrefs = await preferences.GetByCategoryAsync("style", scope: MemoryScope.For("alice"));
        alicePrefs.Should().ContainSingle(p => p.OwnerId == "alice");
    }

    [Fact]
    public async Task ParallelAliceAndBobInvocations_ThroughRealProviderAndTools_DoNotLeakOwnerScope()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var ownerContext = sp.GetRequiredService<IWritableMemoryOwnerContext>();
        var memoryProvider = sp.GetRequiredService<Neo4jMemoryContextProvider>();
        var tools = sp.GetRequiredService<MemoryToolFactory>().CreateAIFunctions();

        var aliceAgent = new ScriptedInnerAgent(memoryProvider, tools, "ok", factSubject: "alice", factObject: "alice-marker")
            .WithMemoryOwnerScoping(ownerContext);
        var bobAgent = new ScriptedInnerAgent(memoryProvider, tools, "ok", factSubject: "bob", factObject: "bob-marker")
            .WithMemoryOwnerScoping(ownerContext);

        var aliceSession = new TestAgentSession().WithMemoryIdentity(userId: "alice", sessionId: "sess-a", conversationId: "conv-a");
        var bobSession = new TestAgentSession().WithMemoryIdentity(userId: "bob", sessionId: "sess-b", conversationId: "conv-b");

        await Task.WhenAll(
            aliceAgent.RunAsync("hi", aliceSession),
            bobAgent.RunAsync("hi", bobSession));

        var facts = sp.GetRequiredService<IFactRepository>();
        var aliceFact = await facts.FindByTripleAsync("alice", "ran_as", "alice-marker", scope: MemoryScope.For("alice"));
        var bobFact = await facts.FindByTripleAsync("bob", "ran_as", "bob-marker", scope: MemoryScope.For("bob"));

        aliceFact.Should().NotBeNull();
        aliceFact!.OwnerId.Should().Be("alice");
        bobFact.Should().NotBeNull();
        bobFact!.OwnerId.Should().Be("bob");
    }

    private sealed class TestAgentSession : AgentSession;

    // A minimal AIAgent standing in for MAF's real ChatClientAgent: its RunCoreAsync drives the real
    // Neo4jMemoryContextProvider's public InvokingAsync/InvokedAsync entry points (recall + persistence)
    // and invokes a real MemoryToolFactory AIFunction (simulating a model-invoked tool call) in between --
    // the exact shape a genuine tool-calling loop has, without needing a scripted IChatClient.
    private sealed class ScriptedInnerAgent : AIAgent
    {
        private readonly Neo4jMemoryContextProvider _memoryProvider;
        private readonly IReadOnlyList<AIFunction> _tools;
        private readonly string _responseText;
        private readonly string _factSubject;
        private readonly string _factObject;

        public ScriptedInnerAgent(
            Neo4jMemoryContextProvider memoryProvider,
            IReadOnlyList<AIFunction> tools,
            string responseText,
            string factSubject = "Ruaidhri",
            string factObject = "tea")
        {
            _memoryProvider = memoryProvider;
            _tools = tools;
            _responseText = responseText;
            _factSubject = factSubject;
            _factObject = factObject;
        }

        protected override async Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var requestMessages = messages.ToList();

            // Pre-run: passive recall, via the provider's real public entry point.
            var invokingContext = new AIContextProvider.InvokingContext(this, session!, new AIContext { Messages = requestMessages });
            await _memoryProvider.InvokingAsync(invokingContext, cancellationToken).ConfigureAwait(false);

            // Simulated model-invoked tool call.
            var rememberFact = _tools.First(t => t.Name == "remember_fact");
            var predicate = _factSubject == "Ruaidhri" ? "likes" : "ran_as";
            await rememberFact.InvokeAsync(
                new AIFunctionArguments(new Dictionary<string, object?>
                {
                    ["subject"] = _factSubject,
                    ["predicate"] = predicate,
                    ["object"] = _factObject,
                }!),
                cancellationToken).ConfigureAwait(false);

            var responseMessages = new List<ChatMessage> { new(ChatRole.Assistant, _responseText) };

            // Post-run: persistence + extraction, via the provider's real public entry point.
            var invokedContext = new AIContextProvider.InvokedContext(this, session!, requestMessages, responseMessages);
            await _memoryProvider.InvokedAsync(invokedContext, cancellationToken).ConfigureAwait(false);

            return new AgentResponse(new ChatMessage(ChatRole.Assistant, _responseText));
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session,
            AgentRunOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await RunCoreAsync(messages, session, options, cancellationToken).ConfigureAwait(false);
            yield return new AgentResponseUpdate(ChatRole.Assistant, response.Text);
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentSession>(new TestAgentSession());

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(default(JsonElement));
    }

    // The default Stub*Extractor registrations produce no output (Phase-1 no-ops); this deterministic
    // extractor always turns a stored message into one preference, matching
    // ExtractionOwnerStampIntegrationTests.cs's convention.
    private sealed class DeterministicPreferenceExtractor : IPreferenceExtractor
    {
        public Task<IReadOnlyList<ExtractedPreference>> ExtractAsync(
            IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedPreference>>(
            [
                new ExtractedPreference { Category = "style", PreferenceText = "prefers dark mode", Confidence = 0.95 },
            ]);
    }
}
