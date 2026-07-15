using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.McpServer.Tools;
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
