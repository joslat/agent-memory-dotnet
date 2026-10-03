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
