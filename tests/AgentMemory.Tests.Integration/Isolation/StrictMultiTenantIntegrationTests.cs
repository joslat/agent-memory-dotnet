using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.McpServer;
using AgentMemory.McpServer.Tools;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Isolation;

/// <summary>
/// Live-Neo4j proof of #100's <see cref="MemoryIsolationMode.StrictMultiTenant"/> contract through a
/// real, fully-wired DI container: an unscoped call to any Tenant-access entry point fails closed
/// (<see cref="MemoryOwnerScopeRequiredException"/>) before Neo4j is ever touched, and concurrent
/// Alice/Bob requests sharing the same singleton <see cref="IMemoryOwnerContext"/> never leak into each
/// other. Closes the #100 acceptance criteria that unit tests (<c>DefaultMemoryIsolationPolicyTests</c>)
/// can't reach: real DI wiring end to end, and true concurrency (not just nested scope-disposal order).
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class StrictMultiTenantIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    public StrictMultiTenantIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(
            configureMemory: o => o.Isolation.Mode = MemoryIsolationMode.StrictMultiTenant,
            configureNeo4j: o =>
            {
                o.Uri = _fixture.ConnectionString;
                o.Username = _fixture.User;
                o.Password = _fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(
                sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(),
                Neo4jIntegrationFixture.TestEmbeddingDimensions));
        // Deliberately NOT calling AddGraphRagAdapter here: registering IGraphRagContextSource in this
        // SHARED container would make MemoryContextAssembler's optional IGraphRagContextSource? ctor
        // param eagerly construct Neo4jGraphRagContextSource for every other test in this class too --
        // and that requires IDriver, which AddNeo4jAgentMemory does not register directly (only via
        // INeo4jDriverFactory). The GraphRAG isolation test below builds Neo4jGraphRagContextSource
        // directly instead, scoped to just that one test.

        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task ExtractAsync_Unscoped_ThrowsBeforeAnyExtractionOrPersistence()
    {
        using var scope = _provider.CreateScope();
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();

        var act = () => pipeline.ExtractAsync(new ExtractionRequest
        {
            SessionId = "strict-session",
            Messages =
            [
                new Message
                {
                    MessageId = $"m-{Guid.NewGuid():N}",
                    ConversationId = "strict-conv",
                    SessionId = "strict-session",
                    Role = "user",
                    Content = "irrelevant",
                    TimestampUtc = DateTimeOffset.UtcNow,
                },
            ],
            // No UserId: unscoped, must fail closed under StrictMultiTenant -- resolved before the
            // extraction stage or Neo4j persistence ever runs (proven at the unit level in
            // MemoryExtractionPipelineTests; this proves the real DI wiring actually delivers it).
        });

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task AssembleContextAsync_Unscoped_ThrowsBeforeTouchingNeo4j()
    {
        using var scope = _provider.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>();

        var act = () => assembler.AssembleContextAsync(new RecallRequest
        {
            SessionId = "strict-session",
            Query = "anything",
            // No UserId.
        });

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task SearchMemoryAsync_Unscoped_ReturnsFailureResult_WithoutThrowing()
    {
        // MemoryQueryFacade is the LLM-tool-invocation surface: its ExecuteAsync wrapper deliberately
        // catches every non-cancellation exception and converts it to MemoryQueryResult.Failed rather
        // than letting it propagate, so the isolation gate surfaces here as a failed Result, not a throw.
        using var scope = _provider.CreateScope();
        var facade = scope.ServiceProvider.GetRequiredService<IMemoryQueryFacade>();

        var result = await facade.SearchMemoryAsync("anything"); // no owner scope set

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("requires an owner scope");
    }

    [Fact]
    public async Task LongTermMemoryService_InvalidateFactAsync_Unscoped_Throws()
    {
        using var scope = _provider.CreateScope();
        var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();

        var act = () => longTerm.InvalidateFactAsync("does-not-matter", scope: null);

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task MaintenanceTools_MemoryInvalidate_Unscoped_ThrowsThroughRealDIContainer()
    {
        // MaintenanceTools does zero isolation logic of its own -- it just passes a possibly-null
        // MemoryScope through to ILongTermMemoryService, where #100's policy actually lives. This proves
        // that transitive enforcement holds through the real, fully-wired container.
        using var scope = _provider.CreateScope();
        var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();

        var act = () => MaintenanceTools.MemoryInvalidate(longTerm, type: "fact", id: "does-not-matter");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task MaintenanceTools_MemorySupersede_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var longTerm = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();

        var act = () => MaintenanceTools.MemorySupersede(longTerm, type: "fact", loserId: "loser", winnerId: "winner");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task ReasoningMemoryService_ListTracesAsync_Unscoped_Throws()
    {
        using var scope = _provider.CreateScope();
        var reasoning = scope.ServiceProvider.GetRequiredService<IReasoningMemoryService>();

        var act = () => reasoning.ListTracesAsync("strict-session");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    // ── #100 Stage 2: the ~9 previously-uncovered MCP tools (CoreMemoryTools/EntityTools), backed by
    // LongTermMemoryService, and GraphRAG now fail closed too. ──

    [Fact]
    public async Task CoreMemoryTools_MemoryAddEntity_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => CoreMemoryTools.MemoryAddEntity(
            sp.GetRequiredService<ILongTermMemoryService>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IClock>(),
            Options.Create(new AgentMemoryMcpOptions()),
            Options.Create(new LongTermMemoryOptions()),
            name: "Alice", type: "Person");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task CoreMemoryTools_MemoryAddPreference_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => CoreMemoryTools.MemoryAddPreference(
            sp.GetRequiredService<ILongTermMemoryService>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IClock>(),
            Options.Create(new AgentMemoryMcpOptions()),
            Options.Create(new LongTermMemoryOptions()),
            category: "style", preferenceText: "concise answers");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task CoreMemoryTools_MemoryAddFact_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => CoreMemoryTools.MemoryAddFact(
            sp.GetRequiredService<ILongTermMemoryService>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IClock>(),
            Options.Create(new AgentMemoryMcpOptions()),
            Options.Create(new LongTermMemoryOptions()),
            subject: "Alice", predicate: "works_at", factObject: "Acme");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task EntityTools_MemoryCreateRelationship_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => EntityTools.MemoryCreateRelationship(
            sp.GetRequiredService<ILongTermMemoryService>(),
            sp.GetRequiredService<IIdGenerator>(),
            sp.GetRequiredService<IClock>(),
            Options.Create(new AgentMemoryMcpOptions()),
            sourceEntityId: "e1", targetEntityId: "e2", relationshipType: "KNOWS");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task EntityTools_MemoryGetEntity_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => EntityTools.MemoryGetEntity(
            sp.GetRequiredService<ILongTermMemoryService>(), name: "Alice");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task EntityTools_MemoryRecordEntityFeedback_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => EntityTools.MemoryRecordEntityFeedback(
            sp.GetRequiredService<ILongTermMemoryService>(), entityId: "e1", positive: true);

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task EntityTools_MemoryGetEntityProvenance_Unscoped_ThrowsThroughRealDIContainer()
    {
        // Adversarial-review finding: this tool bypasses ILongTermMemoryService (it reads
        // IExtractorRepository directly), so it needed its own explicit isolation-policy wiring rather
        // than inheriting the gate the other EntityTools methods get for free.
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => EntityTools.MemoryGetEntityProvenance(
            sp.GetRequiredService<IExtractorRepository>(),
            sp.GetRequiredService<IMemoryIsolationPolicy>(),
            entityId: "e1");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task GraphRagContextSource_Unscoped_ThrowsBeforeRetrieverRuns()
    {
        // Built directly (not resolved from the shared container -- see the comment in InitializeAsync)
        // using the fixture's real IDriver and the container's real, StrictMultiTenant-configured
        // IMemoryIsolationPolicy, so the isolation gate under test is the genuine DI-registered policy.
        using var scope = _provider.CreateScope();
        var graphRag = new Neo4jGraphRagContextSource(
            _fixture.Driver,
            scope.ServiceProvider.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
            Options.Create(new GraphRagOptions { IndexName = "entity_embedding_idx", SearchMode = GraphRagSearchMode.Vector }),
            NullLogger<Neo4jGraphRagContextSource>.Instance,
            ranking: null,
            isolationPolicy: scope.ServiceProvider.GetRequiredService<IMemoryIsolationPolicy>());

        var act = () => graphRag.GetContextAsync(new GraphRagContextRequest
        {
            SessionId = "strict-session", Query = "anything", TopK = 3,
            // No UserId.
        });

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task CoreMemoryTools_MemoryAddFact_AliceCannotBeReadByBob()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var longTerm = sp.GetRequiredService<ILongTermMemoryService>();

        await CoreMemoryTools.MemoryAddFact(
            longTerm, sp.GetRequiredService<IIdGenerator>(), sp.GetRequiredService<IClock>(),
            Options.Create(new AgentMemoryMcpOptions()), Options.Create(new LongTermMemoryOptions()),
            subject: "alice-secret", predicate: "is", factObject: "private", userId: "alice");

        var aliceFacts = await longTerm.GetFactsBySubjectAsync("alice-secret", MemoryScope.For("alice"));
        var bobFacts = await longTerm.GetFactsBySubjectAsync("alice-secret", MemoryScope.For("bob"));

        aliceFacts.Should().ContainSingle(f => f.Object == "private");
        bobFacts.Should().BeEmpty("Bob must never see Alice's privately-owned fact");
    }

    [Fact]
    public async Task ConversationTools_MemoryGetConversation_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => ConversationTools.MemoryGetConversation(
            sp.GetRequiredService<IShortTermMemoryService>(),
            sp.GetRequiredService<IConversationRepository>(),
            sp.GetRequiredService<IMemoryIsolationPolicy>(),
            conversationId: "does-not-matter");

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task ConversationTools_MemoryListSessions_Unscoped_ThrowsThroughRealDIContainer()
    {
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;

        var act = () => ConversationTools.MemoryListSessions(
            sp.GetRequiredService<IConversationRepository>(),
            Options.Create(new AgentMemoryMcpOptions()),
            sp.GetRequiredService<IMemoryIsolationPolicy>());

        await act.Should().ThrowAsync<MemoryOwnerScopeRequiredException>();
    }

    [Fact]
    public async Task ParallelAliceAndBobRequests_ThroughSharedOwnerContext_DoNotLeakAcrossEachOther()
    {
        // The issue's explicit acceptance criterion: "Parallel Alice and Bob invocations do not leak
        // owner context." IMemoryOwnerContext/IWritableMemoryOwnerContext is registered as a singleton
        // (AsyncLocal-backed), so both concurrent calls below share the SAME instance -- exactly the
        // shape a pooled/singleton ambient context has in a real concurrent-request host. Only
        // AsyncLocal's per-logical-call-context isolation can keep them from leaking into each other.
        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var ownerContext = sp.GetRequiredService<IWritableMemoryOwnerContext>();
        var facade = sp.GetRequiredService<IMemoryQueryFacade>();

        async Task RememberAs(string user)
        {
            using (ownerContext.BeginOwnerScope(user))
            {
                await Task.Yield(); // interleave with the other concurrent call before the write lands
                var result = await facade.RememberFactAsync(user, "ran_as", user);
                result.Success.Should().BeTrue();
            }
        }

        await Task.WhenAll(RememberAs("alice"), RememberAs("bob"));

        var facts = sp.GetRequiredService<IFactRepository>();
        var aliceFact = await facts.FindByTripleAsync("alice", "ran_as", "alice", scope: MemoryScope.For("alice"));
        var bobFact = await facts.FindByTripleAsync("bob", "ran_as", "bob", scope: MemoryScope.For("bob"));

        aliceFact.Should().NotBeNull();
        aliceFact!.OwnerId.Should().Be("alice", "the AsyncLocal owner context must not leak bob's id into alice's concurrent write");
        bobFact.Should().NotBeNull();
        bobFact!.OwnerId.Should().Be("bob", "the AsyncLocal owner context must not leak alice's id into bob's concurrent write");
    }
}
