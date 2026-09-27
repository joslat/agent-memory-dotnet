using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using NSubstitute;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.AgentFramework;

/// <summary>
/// Every extracted memory has a path back to what the user said (external review, 2026-09-27).
/// </summary>
/// <remarks>
/// Each component minted its own fresh id for a caller's message: the chat-history provider stored it
/// under one id, the context provider extracted from a never-stored copy under another. So no
/// <c>EXTRACTED_FROM</c> edge reached the user's words, and with <c>ExtractFromUserMessagesOnly</c>
/// there were no edges at all and every <c>source_message_ids</c> entry pointed at nothing. This drives
/// a REAL <see cref="ChatClientAgent"/> turn (only the model is stubbed) with both providers, against Neo4j.
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ProvenanceReachesTheUsersMessageIntegrationTests : IAsyncLifetime
{
    private const string Said = "I'm Marta, I live in Valencia and I prefer tea.";
    private readonly Neo4jIntegrationFixture _fixture;

    public ProvenanceReachesTheUsersMessageIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private ServiceProvider Services(bool userOnly, bool background)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // Deterministic extractors, registered BEFORE AddNeo4jAgentMemory so they win its TryAdd.
        services.AddSingleton<IEntityExtractor, FromTheUser>();
        services.AddSingleton<IFactExtractor, FromTheUser>();
        services.AddSingleton<IPreferenceExtractor, FromTheUser>();
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
        services.AddAgentMemoryFramework(o =>
        {
            o.AutoExtractOnPersist = true;
            o.ExtractFromUserMessagesOnly = userOnly;
            o.ExtractInBackground = background;
        });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(),
                Neo4jIntegrationFixture.TestEmbeddingDimensions));
        return services.BuildServiceProvider(validateScopes: true);
    }

    private async Task<T> ReadAsync<T>(string cypher, Func<IReadOnlyList<global::Neo4j.Driver.IRecord>, T> map) =>
        await _fixture.TransactionRunner.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher);
            return map(await cursor.ToListAsync());
        });

    private async Task RunOneTurnAsync(bool userOnly, bool background, bool withHistoryProvider)
    {
        await using var provider = Services(userOnly, background);
        using var scope = provider.CreateScope();
        var sp = scope.ServiceProvider;
        var chat = Substitute.For<IChatClient>();
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "Nice to meet you, Marta!"))));
        var agent = chat.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "ProvenanceTestAgent",
            AIContextProviders = [sp.GetRequiredService<Neo4jMemoryContextProvider>()],
            ChatHistoryProvider = withHistoryProvider ? sp.GetRequiredService<Neo4jChatHistoryProvider>() : null,
        });
        var session = (await agent.CreateSessionAsync()).WithMemoryIdentity("alice", "session-1", "conv-1");

        await agent.RunAsync(Said, session);
        await provider.GetRequiredService<IBackgroundExtraction>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    public static TheoryData<bool, bool, bool> Paths => new()
    {
        // userOnly, background, with the chat-history provider as well
        { true, false, true },
        { false, false, true },
        { true, true, true },
        { false, true, true },
        { true, false, false },
        { true, true, false },
    };

    [Theory]
    [MemberData(nameof(Paths))]
    public async Task Every_extracted_memory_has_an_edge_to_the_users_own_message(bool userOnly, bool background, bool withHistoryProvider)
    {
        await RunOneTurnAsync(userOnly, background, withHistoryProvider);

        var userMessages = await ReadAsync(
            $"MATCH (m:Message {{session_id: 'session-1', role: 'user'}}) WHERE m.content = '{Said.Replace("'", "\\'")}' RETURN m.id AS id",
            rows => rows.Select(r => r["id"].As<string>()).ToList());
        userMessages.Should().ContainSingle("exactly one :Message per request message, however many components store it");

        var missing = await ReadAsync(
            """
            MATCH (n) WHERE (n:Fact OR n:Entity OR n:Preference) AND n.owner_id = 'alice'
            OPTIONAL MATCH (n)-[:EXTRACTED_FROM]->(m:Message {role: 'user'})
            WITH n, count(m) AS edges
            RETURN labels(n)[0] AS kind, count(n) AS nodes, sum(CASE WHEN edges = 0 THEN 1 ELSE 0 END) AS without
            """,
            rows => rows.Select(r => (Kind: r["kind"].As<string>(), Nodes: r["nodes"].As<long>(), Without: r["without"].As<long>())).ToList());
        missing.Should().HaveCount(3, "an entity, a fact and a preference were extracted");
        missing.Should().OnlyContain(k => k.Without == 0, "every extracted node reaches the user's message");

        var dangling = await ReadAsync(
            """
            MATCH (n) WHERE (n:Fact OR n:Entity OR n:Preference) AND n.owner_id = 'alice'
            UNWIND coalesce(n.source_message_ids, []) AS id
            OPTIONAL MATCH (m:Message {id: id})
            RETURN count(id) AS ids, count(m) AS resolved
            """,
            rows => (Ids: rows[0]["ids"].As<long>(), Resolved: rows[0]["resolved"].As<long>()));
        dangling.Ids.Should().BePositive();
        dangling.Resolved.Should().Be(dangling.Ids, "every source_message_ids entry resolves to a stored message");
    }

    /// <summary>One entity, fact and preference from the user's own message; nothing from anything else.</summary>
    private sealed class FromTheUser : IEntityExtractor, IFactExtractor, IPreferenceExtractor
    {
        private static bool Said(IReadOnlyList<Message> messages) =>
            messages.Any(m => m.Role == "user" && m.Content.Contains("Marta", StringComparison.Ordinal));

        Task<IReadOnlyList<ExtractedEntity>> IEntityExtractor.ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtractedEntity>>(Said(messages)
                ? [new ExtractedEntity { Name = "Marta", Type = "PERSON", Confidence = 0.95 }] : []);

        Task<IReadOnlyList<ExtractedFact>> IFactExtractor.ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>(Said(messages)
                ? [new ExtractedFact { Subject = "Marta", Predicate = "lives in", Object = "Valencia", Confidence = 0.95 }] : []);

        Task<IReadOnlyList<ExtractedPreference>> IPreferenceExtractor.ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ExtractedPreference>>(Said(messages)
                ? [new ExtractedPreference { Category = "drink", PreferenceText = "prefers tea", Confidence = 0.95 }] : []);
    }
}
