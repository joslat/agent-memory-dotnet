using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Recall;
using AgentMemory.AgentFramework.Tools;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.AgentFramework;

public sealed class Neo4jMemoryContextProviderTests
{
    private readonly IMemoryService _memoryService = Substitute.For<IMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IIdGenerator _idGenerator = Substitute.For<IIdGenerator>();
    private readonly Neo4jMemoryContextProvider _sut;

    public Neo4jMemoryContextProviderTests()
    {
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        _idGenerator.GenerateId().Returns(_ => Guid.NewGuid().ToString("N"));
        _sut = new Neo4jMemoryContextProvider(
            _memoryService,
            _embeddingOrchestrator,
            _clock,
            _idGenerator,
            Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance);
    }

    private static RecallResult EmptyRecall(string sessionId) => new()
    {
        Context = new MemoryContext { SessionId = sessionId, AssembledAtUtc = DateTimeOffset.UtcNow }
    };

    private Neo4jMemoryContextProvider CreateSutWithRecallOptions(RecallOptions recall) =>
        new(
            _memoryService,
            _embeddingOrchestrator,
            _clock,
            _idGenerator,
            Options.Create(new MemoryOptions { Recall = recall }),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance);

    // ── Configured RecallOptions reach native MAF recall (#87) ─────────────

    [Fact]
    public async Task BuildContextAsync_UsesConfiguredRecallOptions()
    {
        var configuredRecall = new RecallOptions
        {
            MaxFacts = 3,
            MaxEntities = 4,
            MinSimilarityScore = 0.85,
            BlendMode = RetrievalBlendMode.MemoryOnly
        };
        var sut = CreateSutWithRecallOptions(configuredRecall);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "What do you know about this customer?") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(request =>
                request.Options!.MaxFacts == 3 &&
                request.Options.MaxEntities == 4 &&
                request.Options.MinSimilarityScore == 0.85 &&
                request.Options.BlendMode == RetrievalBlendMode.MemoryOnly),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_ConfiguredScope_NeverOverridesTheInvocationOwner()
    {
        // A static, globally-configured Scope must not silently override the real, per-invocation owner
        // (#100's isolation policy resolves scope from RecallRequest.UserId) -- this is the exact risk
        // flagged when sequencing #87 after #100.
        var configuredRecall = new RecallOptions { Scope = MemoryScope.For("some-other-owner") };
        var sut = CreateSutWithRecallOptions(configuredRecall);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            "s1", "c1", CancellationToken.None, userId: "alice");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(request => request.Options!.Scope == null && request.UserId == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_NoConfiguredRecallOptions_UsesDefaults()
    {
        // Existing default behavior must remain unchanged when the host does not customize recall options.
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(request =>
                request.Options!.MaxFacts == RecallOptions.Default.MaxFacts &&
                request.Options.MaxEntities == RecallOptions.Default.MaxEntities &&
                request.Options.BlendMode == RecallOptions.Default.BlendMode),
            Arg.Any<CancellationToken>());
    }

    // ── #88: task-aware automatic recall policy ────────────────────────────

    private Neo4jMemoryContextProvider CreateSutWithPolicy(IAutomaticRecallPolicy policy, RecallOptions? recall = null) =>
        new(
            _memoryService,
            _embeddingOrchestrator,
            _clock,
            _idGenerator,
            Options.Create(new MemoryOptions { Recall = recall ?? RecallOptions.Default }),
            Options.Create(new ContextFormatOptions()),
            Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance,
            recallPolicy: policy);

    [Fact]
    public async Task BuildContextAsync_NoPolicySupplied_DefaultsToConfiguredAutomaticRecallPolicy()
    {
        // The optional ctor param defaults internally so DI-less construction (as used throughout this
        // test class) keeps behaving exactly as it did before #88.
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_PolicySkipsRecall_DoesNotCallMemoryService()
    {
        var policy = Substitute.For<IAutomaticRecallPolicy>();
        policy.DecideAsync(Arg.Any<AutomaticRecallContext>(), Arg.Any<CancellationToken>())
            .Returns(AutomaticRecallDecision.Skip);
        var sut = CreateSutWithPolicy(policy);

        var result = await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
        await _memoryService.DidNotReceive().RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
        // A skipped turn must not even generate an embedding -- there is nothing left to search for.
        await _embeddingOrchestrator.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_PolicySelectsCategories_ZeroesExcludedCategoryLimits()
    {
        var configuredRecall = new RecallOptions { MaxFacts = 5, MaxEntities = 4, MaxPreferences = 3, MaxTraces = 2, MaxGraphRagItems = 1 };
        var policy = Substitute.For<IAutomaticRecallPolicy>();
        policy.DecideAsync(Arg.Any<AutomaticRecallContext>(), Arg.Any<CancellationToken>())
            .Returns(new AutomaticRecallDecision
            {
                ShouldRecall = true,
                Categories = AutomaticRecallCategories.Preferences | AutomaticRecallCategories.Facts
            });
        var sut = CreateSutWithPolicy(policy, configuredRecall);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "What do you know about me?") }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r =>
                r.Options!.MaxFacts == 5 &&           // selected -- configured value preserved
                r.Options.MaxPreferences == 3 &&      // selected -- configured value preserved
                r.Options.MaxEntities == 0 &&         // excluded -- zeroed
                r.Options.MaxTraces == 0 &&           // excluded -- zeroed
                r.Options.MaxGraphRagItems == 0),     // excluded -- zeroed
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_PolicySelectsCategories_PreservesMinSimilarityScoreAndBlendMode()
    {
        // Coverage gap flagged in review: the Categories-partial branch of ResolveEffectiveOptions must
        // pass through every RecallOptions field it doesn't explicitly zero -- not just the other Max*
        // fields already covered elsewhere, but also MinSimilarityScore and BlendMode specifically.
        var configuredRecall = new RecallOptions
        {
            MinSimilarityScore = 0.42,
            BlendMode = RetrievalBlendMode.MemoryThenGraphRag
        };
        var policy = Substitute.For<IAutomaticRecallPolicy>();
        policy.DecideAsync(Arg.Any<AutomaticRecallContext>(), Arg.Any<CancellationToken>())
            .Returns(new AutomaticRecallDecision { ShouldRecall = true, Categories = AutomaticRecallCategories.Facts });
        var sut = CreateSutWithPolicy(policy, configuredRecall);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r =>
                r.Options!.MinSimilarityScore == 0.42 && r.Options.BlendMode == RetrievalBlendMode.MemoryThenGraphRag),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_PolicyIntentOverride_AppliesToRecallOptions()
    {
        var policy = Substitute.For<IAutomaticRecallPolicy>();
        policy.DecideAsync(Arg.Any<AutomaticRecallContext>(), Arg.Any<CancellationToken>())
            .Returns(new AutomaticRecallDecision { ShouldRecall = true, Intent = RankingIntent.Analog });
        var sut = CreateSutWithPolicy(policy);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "Find a similar previous incident") }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.Options!.Intent == RankingIntent.Analog),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_PolicyNoIntentOverride_PreservesConfiguredIntent()
    {
        // Intent=null on the decision must not reset a host-configured non-default Intent to Default.
        var configuredRecall = new RecallOptions { Intent = RankingIntent.Latest };
        var policy = Substitute.For<IAutomaticRecallPolicy>();
        policy.DecideAsync(Arg.Any<AutomaticRecallContext>(), Arg.Any<CancellationToken>())
            .Returns(new AutomaticRecallDecision { ShouldRecall = true });
        var sut = CreateSutWithPolicy(policy, configuredRecall);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.Options!.Intent == RankingIntent.Latest),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_PolicyExplicitRecallOptions_UsedVerbatim()
    {
        var explicitOptions = new RecallOptions { MaxFacts = 99, MaxEntities = 0, BlendMode = RetrievalBlendMode.MemoryOnly };
        var policy = Substitute.For<IAutomaticRecallPolicy>();
        policy.DecideAsync(Arg.Any<AutomaticRecallContext>(), Arg.Any<CancellationToken>())
            .Returns(new AutomaticRecallDecision { ShouldRecall = true, RecallOptions = explicitOptions });
        var sut = CreateSutWithPolicy(policy);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r =>
                r.Options!.MaxFacts == 99 && r.Options.MaxEntities == 0 &&
                r.Options.BlendMode == RetrievalBlendMode.MemoryOnly),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_PolicyExplicitRecallOptionsWithScope_ScopeIsStillCleared()
    {
        // Same invariant as #87/#100: even a policy-supplied full RecallOptions override must never let a
        // statically-set Scope reach recall -- scope always comes from the invocation's authenticated userId.
        var explicitOptions = new RecallOptions { Scope = MemoryScope.For("some-other-owner") };
        var policy = Substitute.For<IAutomaticRecallPolicy>();
        policy.DecideAsync(Arg.Any<AutomaticRecallContext>(), Arg.Any<CancellationToken>())
            .Returns(new AutomaticRecallDecision { ShouldRecall = true, RecallOptions = explicitOptions });
        var sut = CreateSutWithPolicy(policy);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None, userId: "alice");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(request => request.Options!.Scope == null && request.UserId == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_PolicyReceivesUserMessagesSessionAndUser()
    {
        var policy = Substitute.For<IAutomaticRecallPolicy>();
        policy.DecideAsync(Arg.Any<AutomaticRecallContext>(), Arg.Any<CancellationToken>())
            .Returns(AutomaticRecallDecision.Recall);
        var sut = CreateSutWithPolicy(policy);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.System, "sys"), new(ChatRole.User, "What do I prefer?") },
            "s1", "c1", CancellationToken.None, userId: "alice");

        await policy.Received(1).DecideAsync(
            Arg.Is<AutomaticRecallContext>(ctx =>
                ctx.SessionId == "s1" && ctx.ConversationId == "c1" && ctx.UserId == "alice" &&
                ctx.Messages.Count == 1 && ctx.Messages[0].Text == "What do I prefer?"),
            Arg.Any<CancellationToken>());
    }

    // ── BuildContextAsync (internal, tested via InternalsVisibleTo) ────────

    [Fact]
    public async Task BuildContextAsync_NoUserMessages_ReturnsEmptyContext()
    {
        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, "You are helpful.")
        };

        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
        await _memoryService.DidNotReceive().RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_UsesEmbeddingOrchestrator_EmbedQueryAsync()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "Tell me about Neo4j.") };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.5f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _embeddingOrchestrator.Received(1)
            .EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.QueryEmbedding != null && r.QueryEmbedding.Length == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_WithUserMessage_CallsRecallAsync()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "What is Neo4j?") };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.SessionId == "s1" && r.Query.Contains("Neo4j")),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_WithUserId_SetsRecallRequestUserId()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "What do I prefer?") };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None, userId: "alice");

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.UserId == "alice"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_WithoutUserId_RecallRequestUserIdIsNull()
    {
        var messages = new List<ChatMessage> { new(ChatRole.User, "What do I prefer?") };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.UserId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BuildContextAsync_WithRecallResults_ReturnsContextMessages()
    {
        var storedMsg = new Message
        {
            MessageId = "m1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Neo4j is a graph database.",
            TimestampUtc = DateTimeOffset.UtcNow
        };
        var recallResult = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1",
                AssembledAtUtc = DateTimeOffset.UtcNow,
                RecentMessages = new MemoryContextSection<Message> { Items = [storedMsg] }
            }
        };
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(recallResult);

        var messages = new List<ChatMessage> { new(ChatRole.User, "Tell me about graph databases.") };
        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().NotBeNullOrEmpty();
        result.Messages!.Any(m => m.Text != null && m.Text.Contains("graph database")).Should().BeTrue();
    }

    [Fact]
    public async Task BuildContextAsync_EmbeddingFails_StillCallsRecall()
    {
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Embedding service unavailable"));
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).RecallAsync(
            Arg.Is<RecallRequest>(r => r.QueryEmbedding == null),
            Arg.Any<CancellationToken>());
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task BuildContextAsync_RecallFails_ReturnsEmptyContext()
    {
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB down"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "Hello") };
        var result = await _sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
    }

    // ── Ambient owner context (IC8) — cross-owner isolation of LLM-invokable tools ────────
    //
    // The agent's mid-turn facade tools (search_memory / remember_*) scope only via the ambient
    // IMemoryOwnerContext. The provider must push the turn's userId into it, or those tools run unscoped
    // (cross-owner read leak) and writes are stored shared/global. We assert the WIRING via a substitute:
    // DefaultMemoryOwnerContext is AsyncLocal-backed, and a value set inside an awaited async method is not
    // observable by the caller afterwards — so the host must own the enclosing scope for the value to reach
    // the tools (documented in docs/reviews/review-2026-06-13-cycle3.md). Here we verify the provider sets it.

    [Fact]
    public async Task BuildContextAsync_WithUserId_PushesOwnerIntoAmbientContext()
    {
        var owner = Substitute.For<IWritableMemoryOwnerContext>();
        var sut = new Neo4jMemoryContextProvider(
            _memoryService, _embeddingOrchestrator, _clock, _idGenerator, Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()), Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance, ownerContext: owner);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        var messages = new List<ChatMessage> { new(ChatRole.User, "What do I prefer?") };
        await sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None, userId: "alice");

        owner.Received().UserId = "alice";
    }

    [Fact]
    public async Task PerformStoreAsync_WithUserId_PushesOwnerIntoAmbientContext()
    {
        var owner = Substitute.For<IWritableMemoryOwnerContext>();
        var sut = new Neo4jMemoryContextProvider(
            _memoryService, _embeddingOrchestrator, _clock, _idGenerator, Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()), Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance, ownerContext: owner);

        await sut.PerformStoreAsync(
            Array.Empty<ChatMessage>(),
            new List<ChatMessage> { new(ChatRole.Assistant, "noted") }, "s1", "c1",
            CancellationToken.None, userId: "bob");

        owner.Received().UserId = "bob";
    }

    [Fact]
    public async Task BuildContextAsync_WithoutUserId_PushesNullOwner_NoStaleBleed()
    {
        // An unowned turn must reset the ambient owner to null (shared), never inherit a previous turn's.
        var owner = Substitute.For<IWritableMemoryOwnerContext>();
        var sut = new Neo4jMemoryContextProvider(
            _memoryService, _embeddingOrchestrator, _clock, _idGenerator, Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()), Options.Create(new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance, ownerContext: owner);
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        owner.Received().UserId = null;
    }

    // ── PerformStoreAsync (internal, tested via InternalsVisibleTo) ────────

    private static readonly DateTimeOffset FixedTime = new(2025, 6, 1, 0, 0, 0, TimeSpan.Zero);

    private Neo4jMemoryContextProvider CreateSut(
        AgentFrameworkOptions? agentOptions = null, MemoryToolFactory? toolFactory = null) =>
        new(
            _memoryService,
            _embeddingOrchestrator,
            _clock,
            _idGenerator,
            Options.Create(new MemoryOptions()),
            Options.Create(new ContextFormatOptions()),
            Options.Create(agentOptions ?? new AgentFrameworkOptions()),
            NullLogger<Neo4jMemoryContextProvider>.Instance,
            toolFactory: toolFactory);

    // ── #86: optional memory-tool exposure via AIContext.Tools ─────────────
    //
    // ExposeMemoryToolsFromContextProvider defaults to false: AddAgentMemoryFramework registers
    // MemoryToolFactory unconditionally, and its tools include write-capable ones (remember_fact,
    // remember_preference) -- so exposure must stay opt-in, never automatic just because the factory
    // exists in DI. Every BuildContextAsync branch must agree on Tools, including the early-return ones
    // (no user messages / recall failure), so a turn with nothing to recall doesn't silently lose tool
    // availability.

    private static MemoryToolFactory CreateToolFactory() =>
        new(Substitute.For<IMemoryQueryFacade>());

    [Fact]
    public async Task BuildContextAsync_ToolExposureDisabledByDefault_DoesNotSetTools()
    {
        var sut = CreateSut(toolFactory: CreateToolFactory());
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        var result = await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        result.Tools.Should().BeNullOrEmpty();
    }

    [Fact]
    public async Task BuildContextAsync_ToolExposureEnabled_WithRecallResults_SetsTools()
    {
        var sut = CreateSut(
            new AgentFrameworkOptions { ExposeMemoryToolsFromContextProvider = true },
            CreateToolFactory());
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        var result = await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "What do you know about me?") },
            "s1", "c1", CancellationToken.None);

        result.Tools.Should().NotBeNullOrEmpty();
        result.Tools!.Select(t => t.Name).Should().Contain(
            ["search_memory", "remember_preference", "remember_fact", "recall_preferences"]);
    }

    [Fact]
    public async Task BuildContextAsync_ToolExposureEnabled_NoUserMessages_StillSetsTools()
    {
        var sut = CreateSut(
            new AgentFrameworkOptions { ExposeMemoryToolsFromContextProvider = true },
            CreateToolFactory());
        var messages = new List<ChatMessage> { new(ChatRole.System, "You are helpful.") };

        var result = await sut.BuildContextAsync(messages, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
        result.Tools.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task BuildContextAsync_ToolExposureEnabled_RecallFails_StillSetsTools()
    {
        var sut = CreateSut(
            new AgentFrameworkOptions { ExposeMemoryToolsFromContextProvider = true },
            CreateToolFactory());
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("DB down"));

        var result = await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        result.Messages.Should().BeNullOrEmpty();
        result.Tools.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task BuildContextAsync_ToolExposureEnabled_NoFactoryRegistered_ToolsIsNull()
    {
        // Flag on, but MemoryToolFactory not supplied (e.g. a host that only calls AddAgentMemoryCore) --
        // must degrade to no tools, never throw.
        var sut = CreateSut(new AgentFrameworkOptions { ExposeMemoryToolsFromContextProvider = true });
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyRecall("s1"));

        var result = await sut.BuildContextAsync(
            new List<ChatMessage> { new(ChatRole.User, "hi") }, "s1", "c1", CancellationToken.None);

        result.Tools.Should().BeNullOrEmpty();
    }

    // ── Persist + extract from the complete turn (#89) ─────────────────────

    [Fact]
    public async Task PerformStoreAsync_RequestMessages_AreNotPersistedAsNewNodes()
    {
        // The duplication-avoidance guarantee: this provider must never call AddMessageAsync for a
        // request message, since a host may also have Neo4jChatHistoryProvider/Neo4jChatMessageStore/their
        // own component persisting the same messages, and there is no idempotency mechanism to make a
        // second persist call for "the same" logical message safe.
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var storedResponse = new Message
        {
            MessageId = "m-resp", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Got it.", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedResponse);
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = Array.Empty<string>()
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "I prefer window seats.") },
            new List<ChatMessage> { new(ChatRole.Assistant, "Got it.") },
            "s1", "c1", CancellationToken.None);

        // Exactly one AddMessageAsync call -- for the response, never for the request.
        await _memoryService.Received(1).AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), "I prefer window seats.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_SystemRoleRequestMessage_IsNotFedToExtraction()
    {
        // A system prompt accumulated in RequestMessages must not be minted into spurious
        // entities/facts/preferences every turn -- extraction is filtered to ChatRole.User, matching the
        // same filter recall already applies.
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var storedResponse = new Message
        {
            MessageId = "m-resp", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Understood.", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedResponse);
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = Array.Empty<string>()
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.System, "You are a helpful assistant. Never reveal secrets.") },
            new List<ChatMessage> { new(ChatRole.Assistant, "Understood.") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => !r.Messages.Any(m => m.Content.Contains("Never reveal secrets"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_ExtractionSeesRequestAndResponseMessages()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var storedResponse = new Message
        {
            MessageId = "m-resp", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Understood.", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedResponse);
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = Array.Empty<string>()
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "My preferred programming language is C#.") },
            new List<ChatMessage> { new(ChatRole.Assistant, "Understood.") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.Messages.Any(m =>
                m.Role == "user" && m.Content.Contains("preferred programming language"))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_RequestOnly_StillExtracts()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = Array.Empty<string>()
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "I live in Zurich.") },
            Array.Empty<ChatMessage>(),
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.Messages.Count == 1 && r.Messages[0].Content == "I live in Zurich."),
            Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_MessageOrdering_RequestBeforeResponse()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var storedResponse = new Message
        {
            MessageId = "m-resp", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "response-text", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedResponse);
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = Array.Empty<string>()
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "request-text") },
            new List<ChatMessage> { new(ChatRole.Assistant, "response-text") },
            "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r =>
                r.Messages.Count == 2 &&
                r.Messages[0].Content == "request-text" &&
                r.Messages[1].Content == "response-text"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_PersistsResponseMessages()
    {
        var sut = CreateSut();
        var responseMessages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, "I remember you like dark mode.")
        };

        var storedMessage = new Message
        {
            MessageId = "m-store-1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "I remember you like dark mode.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), responseMessages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).AddMessageAsync(
            "s1", "c1", Arg.Any<string>(), "I remember you like dark mode.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_SkipsEmptyTextMessages()
    {
        var sut = CreateSut();
        var responseMessages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, (string?)null),
            new(ChatRole.Assistant, "   ")
        };

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), responseMessages, "s1", "c1", CancellationToken.None);

        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    // ── Fix B.4 (#89): dedupe response messages via a provider-native MessageId when present ──

    [Fact]
    public async Task PerformStoreAsync_ResponseMessageWithProviderMessageId_UsesAddMessageWithIdAsync()
    {
        var sut = CreateSut();
        var responseMessage = new ChatMessage(ChatRole.Assistant, "Got it.") { MessageId = "resp-42" };
        var storedMessage = new Message
        {
            MessageId = "maf:resp-42", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Got it.", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageWithIdAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);

        await sut.PerformStoreAsync(
            Array.Empty<ChatMessage>(), new List<ChatMessage> { responseMessage }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).AddMessageWithIdAsync(
            "s1", "c1", "assistant", "Got it.", "maf:resp-42",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_ResponseMessageWithoutProviderMessageId_FallsBackToAddMessageAsync()
    {
        var sut = CreateSut();
        var responseMessage = new ChatMessage(ChatRole.Assistant, "Got it.");
        var storedMessage = new Message
        {
            MessageId = "m-store-1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Got it.", TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);

        await sut.PerformStoreAsync(
            Array.Empty<ChatMessage>(), new List<ChatMessage> { responseMessage }, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).AddMessageAsync(
            "s1", "c1", "assistant", "Got it.",
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageWithIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
    }

    // ── Fix C (#89): non-text content (function calls/results, reasoning) is excluded, explicitly ──

    [Fact]
    public async Task PerformStoreAsync_FunctionCallOnlyResponseMessage_IsExcludedFromPersistenceAndExtraction()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var functionCallMessage = new ChatMessage(
            ChatRole.Assistant, new List<AIContent> { new FunctionCallContent("call-1", "search_memory") });
        _memoryService.ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = Array.Empty<string>()
            });

        await sut.PerformStoreAsync(
            new List<ChatMessage> { new(ChatRole.User, "search for my preferences") },
            new List<ChatMessage> { functionCallMessage },
            "s1", "c1", CancellationToken.None);

        await _memoryService.DidNotReceive().AddMessageAsync(
            Arg.Any<string>(), Arg.Any<string>(), "assistant", Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.DidNotReceive().AddMessageWithIdAsync(
            Arg.Any<string>(), Arg.Any<string>(), "assistant", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>());
        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.Messages.Count == 1 && r.Messages[0].Role == "user"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractEnabled_CallsExtractAndPersistAsync()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Paris is the capital of France.") };
        var storedMessage = new Message
        {
            MessageId = "m-ae-1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Paris is the capital of France.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);
        _memoryService
            .ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = new[] { "m-ae-1" }
            });

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None);

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.SessionId == "s1"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractEnabled_WithUserId_StampsExtractionOwner()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Paris is the capital of France.") };
        var storedMessage = new Message
        {
            MessageId = "m-owner-1", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Paris is the capital of France.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);
        _memoryService
            .ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult
            {
                Entities = Array.Empty<ExtractedEntity>(),
                Facts = Array.Empty<ExtractedFact>(),
                Preferences = Array.Empty<ExtractedPreference>(),
                Relationships = Array.Empty<ExtractedRelationship>(),
                SourceMessageIds = new[] { "m-owner-1" }
            });

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None, userId: "bob");

        await _memoryService.Received(1).ExtractAndPersistAsync(
            Arg.Is<ExtractionRequest>(r => r.SessionId == "s1" && r.UserId == "bob"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractDisabled_DoesNotCallExtractAndPersistAsync()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = false });
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Some content.") };
        var storedMessage = new Message
        {
            MessageId = "m-no-ae", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Some content.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);

        await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None);

        await _memoryService.DidNotReceive().ExtractAndPersistAsync(
            Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PerformStoreAsync_ExceptionInAddMessage_IsCaughtGracefully()
    {
        var sut = CreateSut();
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Boom!") };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("DB down"));

        var act = () => sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task PerformStoreAsync_AutoExtractEnabled_ExceptionInExtraction_IsCaughtGracefully()
    {
        var sut = CreateSut(new AgentFrameworkOptions { AutoExtractOnPersist = true });
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Important data.") };
        var storedMessage = new Message
        {
            MessageId = "m-ext-err", SessionId = "s1", ConversationId = "c1",
            Role = "assistant", Content = "Important data.",
            TimestampUtc = FixedTime
        };
        _memoryService
            .AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .Returns(storedMessage);
        _memoryService
            .ExtractAndPersistAsync(Arg.Any<ExtractionRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new Exception("Extraction engine failed"));

        var act = () => sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── R6-A: caller cancellation must propagate, not be swallowed as empty-context / silent success ──
    // Before the OCE guards, a cancelled turn surfaced as an empty AIContext (BuildContext) or a silent
    // "stored" (PerformStore) — the agent ran on no memory / believed a cancelled write succeeded.

    [Fact]
    public async Task BuildContextAsync_RecallCancelled_PropagatesOperationCanceled()
    {
        var sut = CreateSut();
        var messages = new List<ChatMessage> { new(ChatRole.User, "Tell me about Neo4j.") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.5f });
        _memoryService.RecallAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await sut.BuildContextAsync(messages, "s1", "c1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task BuildContextAsync_EmbeddingCancelled_PropagatesOperationCanceled()
    {
        var sut = CreateSut();
        var messages = new List<ChatMessage> { new(ChatRole.User, "Tell me about Neo4j.") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // The embedding sub-try has its own broad catch; the guard must let cancellation escape rather than
        // log "proceeding without semantic search" and continue.
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await sut.BuildContextAsync(messages, "s1", "c1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task PerformStoreAsync_CallerCancels_PropagatesOperationCanceled()
    {
        var sut = CreateSut();
        var messages = new List<ChatMessage> { new(ChatRole.Assistant, "Important data.") };
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        _memoryService.AddMessageAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = async () => await sut.PerformStoreAsync(Array.Empty<ChatMessage>(), messages, "s1", "c1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
