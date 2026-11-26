using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// G3 (PLAN 40.47), on a real store: an erased owner leaves nothing behind and every other owner is untouched; an export
/// imported under a new owner gives the same recall, live and about the past.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class OwnerDataIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider? _provider;

    public OwnerDataIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        Build();
        return _fixture.CleanDatabaseAsync();
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task An_erased_owner_leaves_nothing_behind_and_others_keep_everything()
    {
        await SayAsync("ana", "I live in Bilbao.");
        await SayAsync("ana", "I moved to Madrid in March.");
        await SayAsync("bob", "I live in Porto.");
        var bobBefore = await CountAsync("bob");

        using var scope = _provider!.CreateScope();
        var erasure = await scope.ServiceProvider.GetRequiredService<IMemoryOwnerDataService>().EraseAsync("ana");

        (await CountAsync("ana")).Should().Be(0, "no node stamped with the owner remains");
        (await ScalarAsync("MATCH (c:Conversation {user_id: 'ana'}) RETURN count(c)")).Should().Be(0);
        (await ScalarAsync("MATCH (m:Message {session_id: 's-ana'}) RETURN count(m)")).Should().Be(0, "the owner's messages go with their conversation");
        erasure.Deleted.Should().ContainKeys("Fact", "Message", "Conversation");
        erasure.Deleted["Fact"].Should().Be(2);
        (await CountAsync("bob")).Should().Be(bobBefore, "another owner is untouched");
        (await LiveCitiesAsync("bob")).Should().Equal(["Porto"]);
    }

    // RVW-ERASE-CONVERSATIONS: the test above seeds its conversations with AddConversationAsync(userId), which no host
    // path calls. The Agent Framework providers save messages inside an owner scope and nothing else; the erase must
    // find those too.
    [Fact]
    public async Task An_erased_owner_s_messages_saved_in_an_owner_scope_go_with_them()
    {
        await SayInOwnerScopeAsync("ana", "c-ana-scoped", "I live in Bilbao.");
        await SayInOwnerScopeAsync("bob", "c-bob-scoped", "I live in Porto.");

        using var scope = _provider!.CreateScope();
        var erasure = await scope.ServiceProvider.GetRequiredService<IMemoryOwnerDataService>().EraseAsync("ana");

        (await ScalarAsync("MATCH (m:Message {conversation_id: 'c-ana-scoped'}) RETURN count(m)")).Should().Be(0,
            "a message saved for an owner is erased with the owner");
        (await ScalarAsync("MATCH (c:Conversation {id: 'c-ana-scoped'}) RETURN count(c)")).Should().Be(0);
        erasure.Deleted.Should().ContainKeys("Message", "Conversation");
        (await ScalarAsync("MATCH (m:Message {conversation_id: 'c-bob-scoped'}) RETURN count(m)")).Should().Be(1, "another owner keeps theirs");
        (await ScalarAsync("MATCH (c:Conversation {id: 'c-bob-scoped'}) RETURN c.user_id = 'bob'")).Should().Be(1);
    }

    [Fact]
    public async Task A_conversation_one_owner_holds_takes_no_message_from_another_and_an_ownerless_one_is_adopted()
    {
        await SayInOwnerScopeAsync("ana", "c-held", "I live in Bilbao.");
        var refused = () => SayInOwnerScopeAsync("bob", "c-held", "I live in Porto.");
        await refused.Should().ThrowAsync<UnauthorizedAccessException>("bob may not write into ana's conversation");
        (await ScalarAsync("MATCH (m:Message {conversation_id: 'c-held'}) RETURN count(m)")).Should().Be(1, "nothing of bob's was written");

        await SayInOwnerScopeAsync(null, "c-open", "Hello.");
        (await ScalarAsync("MATCH (c:Conversation {id: 'c-open'}) WHERE c.user_id IS NULL RETURN count(c)")).Should().Be(1,
            "a write with no owner leaves the conversation without one, as before");
        await SayInOwnerScopeAsync("ana", "c-open", "I live in Bilbao.");
        (await ScalarAsync("MATCH (c:Conversation {id: 'c-open'}) RETURN c.user_id = 'ana'")).Should().Be(1,
            "the first owned write adopts an ownerless conversation");
    }

    [Fact]
    public async Task A_batch_into_a_conversation_another_owner_holds_is_refused_whole()
    {
        await SayInOwnerScopeAsync("ana", "c-held-batch", "I live in Bilbao.");
        var refused = () => SayBatchInOwnerScopeAsync("bob", "c-held-batch", "One.", "Two.");
        await refused.Should().ThrowAsync<UnauthorizedAccessException>();
        (await ScalarAsync("MATCH (m:Message {conversation_id: 'c-held-batch'}) RETURN count(m)")).Should().Be(1);

        await SayBatchInOwnerScopeAsync("bob", "c-bob-batch", "One.", "Two.");
        (await ScalarAsync("MATCH (c:Conversation {id: 'c-bob-batch'}) RETURN c.user_id = 'bob'")).Should().Be(1);
    }

    [Fact]
    public async Task An_export_imported_under_a_new_owner_gives_the_same_recall()
    {
        await SayAsync("ana", "I live in Bilbao.");
        await SayAsync("ana", "I moved to Madrid in March.");

        using var scope = _provider!.CreateScope();
        var owners = scope.ServiceProvider.GetRequiredService<IMemoryOwnerDataService>();
        var export = await owners.ExportAsync("ana");
        // Through JSON, as a file would carry it.
        var carried = JsonSerializer.Deserialize<OwnerMemoryExport>(JsonSerializer.Serialize(export))!;
        var imported = await owners.ImportAsync(carried, "carla");

        imported.Written["facts"].Should().Be(2);
        imported.Written["supersessions"].Should().Be(1);
        (await LiveCitiesAsync("carla")).Should().Equal(await LiveCitiesAsync("ana"));
        (await CitiesAsOfAsync("carla", Now.AddYears(-6))).Should().Equal(await CitiesAsOfAsync("ana", Now.AddYears(-6)),
            "the closing came across: the past answers the same");
        (await LiveCitiesAsync("carla")).Should().Equal(["Madrid"]);
    }

    /// <summary>
    /// The review's finding: a merged-away entity came across live beside its survivor, and a fact still linked to it
    /// (data from before F8 moved links on merge) was linked, in the same store, to the original owner's entity.
    /// </summary>
    [Fact]
    public async Task An_import_brings_no_merged_away_entity_back_and_joins_no_two_owners()
    {
        await SayAsync("ana", "I live in Bilbao.");
        await using (var session = _fixture.Driver.AsyncSession())
            await session.RunAsync(@"
                MATCH (f:Fact {owner_id: 'ana'})
                CREATE (old:Entity {id: 'e-pruya', name: 'Pruya', type: 'PERSON', owner_id: 'ana', merged_into: 'e-priya',
                                    invalidated_at: datetime(), created_at: datetime(), confidence: 0.9})
                CREATE (:Entity {id: 'e-priya', name: 'Priya', type: 'PERSON', owner_id: 'ana', created_at: datetime(), confidence: 0.9})
                CREATE (f)-[:ABOUT]->(old)");

        using var scope = _provider!.CreateScope();
        var owners = scope.ServiceProvider.GetRequiredService<IMemoryOwnerDataService>();
        var export = await owners.ExportAsync("ana");
        await owners.ImportAsync(export, "carla");

        export.Entities.Select(e => e.Name).Should().Equal(["Priya"], "the merged-away entity is not exported");
        (await ScalarAsync("MATCH (e:Entity {owner_id: 'carla', name: 'Pruya'}) RETURN count(e)")).Should().Be(0);
        (await scope.ServiceProvider.GetRequiredService<IMemoryIntegrityService>().CheckAsync("carla")).Passed
            .Should().BeTrue("no imported link reaches another owner's entity");
    }

    // RVW-CONVERSATION-OWNER-BACKFILL: conversations written before the message paths stamped an owner have none, so the
    // erase missed them. The backfill gives one the owner its memories came from, lists the rest, and changes nothing twice.
    [Fact]
    public async Task Conversations_written_before_owners_were_stamped_get_the_one_owner_their_memories_came_from()
    {
        await SayAsync("ana", "I live in Bilbao.");
        await SayAsync("bob", "I live in Porto.");
        await RunAsync("MATCH (c:Conversation) REMOVE c.user_id");   // as written before the stamp
        await RunAsync("""
            CREATE (shared:Conversation {id: 'c-shared', session_id: 's-shared'}),
                   (shared)-[:HAS_MESSAGE]->(:Message {id: 'm-shared-ana', conversation_id: 'c-shared', session_id: 's-shared', content: 'one'}),
                   (shared)-[:HAS_MESSAGE]->(:Message {id: 'm-shared-bob', conversation_id: 'c-shared', session_id: 's-shared', content: 'two'}),
                   (:Conversation {id: 'c-none', session_id: 's-none'})
                       -[:HAS_MESSAGE]->(:Message {id: 'm-none', conversation_id: 'c-none', session_id: 's-none', content: 'three'})
            """);
        await RunAsync("""
            MATCH (fa:Fact {owner_id: 'ana'}) WITH fa LIMIT 1
            MATCH (fb:Fact {owner_id: 'bob'}) WITH fa, fb LIMIT 1
            MATCH (ma:Message {id: 'm-shared-ana'}), (mb:Message {id: 'm-shared-bob'})
            CREATE (fa)-[:EXTRACTED_FROM]->(ma), (fb)-[:EXTRACTED_FROM]->(mb)
            """);
        using var scope = _provider!.CreateScope();
        var backfill = scope.ServiceProvider.GetRequiredService<Neo4jConversationOwnerBackfill>();

        var dryRun = await backfill.RunAsync(apply: false);
        dryRun.Attributed.Select(e => $"{e.ConversationId}:{string.Join(",", e.Owners)}").Should().Equal("c-ana:ana", "c-bob:bob");
        dryRun.WithoutOwner.Should().Equal("c-none");
        dryRun.SeveralOwners.Should().ContainSingle().Which.Should().BeEquivalentTo(new { ConversationId = "c-shared", Owners = new[] { "ana", "bob" } });
        (await ScalarAsync("MATCH (c:Conversation) WHERE c.user_id IS NOT NULL RETURN count(c)")).Should().Be(0, "a dry run writes nothing");

        var applied = await backfill.RunAsync(apply: true);
        applied.Attributed.Should().HaveCount(2);
        (await ScalarAsync("MATCH (c:Conversation {id: 'c-ana', user_id: 'ana'}) RETURN count(c)")).Should().Be(1);
        (await ScalarAsync("MATCH (c:Conversation {id: 'c-bob', user_id: 'bob'}) RETURN count(c)")).Should().Be(1);
        (await ScalarAsync("MATCH (c:Conversation) WHERE c.id IN ['c-shared', 'c-none'] AND c.user_id IS NULL RETURN count(c)"))
            .Should().Be(2, "no owner, or several, is not guessed");

        var again = await backfill.RunAsync(apply: true);
        again.Attributed.Should().BeEmpty("a conversation with an owner is never touched again");
        again.WithoutOwner.Should().Equal("c-none");
        again.SeveralOwners.Should().ContainSingle();

        await scope.ServiceProvider.GetRequiredService<IMemoryOwnerDataService>().EraseAsync("ana");
        (await ScalarAsync("MATCH (m:Message {conversation_id: 'c-ana'}) RETURN count(m)")).Should().Be(0, "the erase now finds the backfilled transcript");
        (await ScalarAsync("MATCH (m:Message {conversation_id: 'c-bob'}) RETURN count(m)")).Should().Be(1);
        (await ScalarAsync("MATCH (m:Message {conversation_id: 'c-shared'}) RETURN count(m)")).Should().Be(2, "a conversation of several owners is not one owner's to erase");
    }

    private void Build()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFactExtractor, ScriptedFacts>();
        services.AddNeo4jAgentMemory(
            new MemoryOptions { Extraction = { SupersedeReplacedFacts = true, BitemporalChanges = true } },
            configureNeo4j: o =>
            {
                o.Uri = _fixture.ConnectionString;
                o.Username = _fixture.User;
                o.Password = _fixture.Password;
                o.Database = "neo4j";
                o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
            });
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(), Neo4jIntegrationFixture.TestEmbeddingDimensions));
        _provider = services.BuildServiceProvider(validateScopes: true);
    }

    private async Task SayAsync(string owner, string text)
    {
        using var scope = _provider!.CreateScope();
        var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
        await shortTerm.AddConversationAsync($"c-{owner}", $"s-{owner}", userId: owner);
        var message = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = $"m-{Guid.NewGuid():N}", ConversationId = $"c-{owner}", SessionId = $"s-{owner}",
            Role = "user", Content = text, TimestampUtc = DateTimeOffset.UtcNow,
        });
        await pipeline.ExtractAsync(new ExtractionRequest { SessionId = $"s-{owner}", UserId = owner, Messages = [message] });
    }

    /// <summary>As the Agent Framework providers save a message: inside an owner scope, with no conversation call.</summary>
    private async Task SayInOwnerScopeAsync(string? owner, string conversationId, string text)
    {
        using var scope = _provider!.CreateScope();
        using var ownerScope = scope.ServiceProvider.GetRequiredService<IWritableMemoryOwnerContext>().BeginOwnerScope(owner);
        await scope.ServiceProvider.GetRequiredService<IMemoryService>()
            .AddMessageAsync($"s-{conversationId}", conversationId, "user", text);
    }

    private async Task SayBatchInOwnerScopeAsync(string owner, string conversationId, params string[] texts)
    {
        using var scope = _provider!.CreateScope();
        using var ownerScope = scope.ServiceProvider.GetRequiredService<IWritableMemoryOwnerContext>().BeginOwnerScope(owner);
        var at = DateTimeOffset.UtcNow;
        await scope.ServiceProvider.GetRequiredService<IMemoryService>().AddMessagesAsync(
            [.. texts.Select((text, i) => new Message
            {
                MessageId = $"m-{Guid.NewGuid():N}", ConversationId = conversationId, SessionId = $"s-{conversationId}",
                Role = "user", Content = text, TimestampUtc = at.AddMilliseconds(i),
            })]);
    }

    private static RecallRequest Question(string owner) => new()
    {
        SessionId = $"ask-{owner}", UserId = owner, Query = "Where does the user live?",
        Options = new RecallOptions
        {
            MaxRecentMessages = 0, MaxRelevantMessages = 0, MaxEntities = 0, MaxPreferences = 0, MaxTraces = 0,
            MaxFacts = 10, MinSimilarityScore = 0,
        },
    };

    private async Task<IReadOnlyList<string>> LiveCitiesAsync(string owner)
    {
        using var scope = _provider!.CreateScope();
        var context = (await scope.ServiceProvider.GetRequiredService<IMemoryService>().RecallAsync(Question(owner))).Context;
        return [.. context.RelevantFacts.Items.Where(f => f.Predicate == "lives in").Select(f => f.Object).Order(StringComparer.Ordinal)];
    }

    private async Task<IReadOnlyList<string>> CitiesAsOfAsync(string owner, DateTimeOffset validAsOf)
    {
        using var scope = _provider!.CreateScope();
        var context = (await scope.ServiceProvider.GetRequiredService<IMemoryService>()
            .RecallAsOfAsync(Question(owner), validAsOf, DateTimeOffset.UtcNow)).Context;
        return [.. context.RelevantFacts.Items.Where(f => f.Predicate == "lives in").Select(f => f.Object).Order(StringComparer.Ordinal)];
    }

    private Task<long> CountAsync(string owner) => ScalarAsync($"MATCH (n) WHERE n.owner_id = '{owner}' RETURN count(n)");

    private async Task RunAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }

    private async Task<long> ScalarAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher);
        return ValueExtensions.As<long>((await cursor.SingleAsync())[0]);
    }

    private sealed class ScriptedFacts : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>([.. messages.Select(m => m.Content switch
            {
                "I live in Bilbao." => Lives("Bilbao") with { ValidFrom = Now.AddYears(-16), ValidFromPrecision = DatePrecision.Year },
                "I moved to Madrid in March." => Lives("Madrid") with { ValidFrom = Now.AddMonths(-7), ValidFromPrecision = DatePrecision.Month },
                "I live in Porto." => Lives("Porto"),
                _ => throw new InvalidOperationException($"No script for '{m.Content}'."),
            })]);

        private static ExtractedFact Lives(string city) =>
            new() { Subject = "user", Predicate = "lives in", Object = city, Confidence = 0.95 };
    }
}
