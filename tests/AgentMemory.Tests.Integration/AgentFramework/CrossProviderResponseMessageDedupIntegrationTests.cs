using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.AgentFramework;

/// <summary>
/// Live-Neo4j proof of #89's remaining acceptance criterion: enabling a chat-history provider alongside
/// the memory context provider does not create duplicate persisted messages -- at least when the
/// underlying IChatClient stamps a provider-native <see cref="ChatMessage.MessageId"/> on the response, as
/// many production clients do. Simulates two independently-configured MAF integration components
/// (<see cref="Neo4jMemoryContextProvider"/> and <see cref="Neo4jChatHistoryProvider"/>) both observing the
/// same underlying model response for one turn.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class CrossProviderResponseMessageDedupIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    public CrossProviderResponseMessageDedupIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();

        var services = new ServiceCollection();
        services.AddLogging();

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
        services.AddAgentMemoryFramework(o => o.AutoExtractOnPersist = false);
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
    public async Task TwoProvidersPersistingTheSameResponseMessage_ConvergeOnOneNode()
    {
        const string sessionId = "session-dedup";
        const string conversationId = "conv-dedup";

        // Both providers observe a response message carrying the SAME provider-native MessageId -- as they
        // would if a host configured both on one agent and the underlying model call produced one real
        // response, delivered to both InvokedContext callbacks for the same invocation. Deliberately
        // DIFFERENT text per provider (a scenario that couldn't genuinely arise in production, since it's
        // the same underlying response) so a regression back to CREATE-based persistence is caught hard:
        // it would produce a second node carrying the second provider's text, not just an extra copy of
        // identical content that a content-based assertion could miss.
        var firstObserved = new ChatMessage(ChatRole.Assistant, "Got it.") { MessageId = "resp-shared-1" };
        var secondObserved = new ChatMessage(ChatRole.Assistant, "a different second call must not overwrite this")
        {
            MessageId = "resp-shared-1"
        };

        using (var scope1 = _provider.CreateScope())
        {
            var contextProvider = scope1.ServiceProvider.GetRequiredService<Neo4jMemoryContextProvider>();
            await contextProvider.PerformStoreAsync(
                requestMessages: [new ChatMessage(ChatRole.User, "hello")],
                responseMessages: [firstObserved],
                sessionId: sessionId,
                conversationId: conversationId,
                cancellationToken: CancellationToken.None);
        }

        using (var scope2 = _provider.CreateScope())
        {
            var historyProvider = scope2.ServiceProvider.GetRequiredService<Neo4jChatHistoryProvider>();
            await historyProvider.PerformStoreAsync(
                requestMessages: [new ChatMessage(ChatRole.User, "hello")],
                responseMessages: [secondObserved],
                sessionId: sessionId,
                conversationId: conversationId,
                cancellationToken: CancellationToken.None);
        }

        using var readScope = _provider.CreateScope();
        var memoryService = readScope.ServiceProvider.GetRequiredService<IMemoryService>();
        var recallResult = await memoryService.RecallAsync(new Abstractions.Domain.RecallRequest
        {
            SessionId = sessionId,
            Query = string.Empty,
            Options = new Abstractions.Options.RecallOptions { MaxRecentMessages = 50 }
        });

        // Total messages: 1 request (persisted only by Neo4jChatHistoryProvider; Neo4jMemoryContextProvider
        // deliberately never persists request messages) + 1 response. A regression back to CREATE-based
        // response persistence would show 3 (two separate response nodes), not 2.
        recallResult.Context.RecentMessages.Items.Should().HaveCount(2,
            "both providers persisted a response message sharing the same provider-native MessageId, so " +
            "they must converge on exactly one :Message node instead of each creating their own");

        var responseNode = recallResult.Context.RecentMessages.Items.Should()
            .ContainSingle(m => m.Role == "assistant").Subject;
        responseNode.Content.Should().Be("Got it.",
            "first-write-wins: the second provider's persist call must not overwrite the first provider's content");
    }
}
