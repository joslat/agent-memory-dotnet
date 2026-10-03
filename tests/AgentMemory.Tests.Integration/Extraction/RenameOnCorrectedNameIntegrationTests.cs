using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using Neo4j.Driver;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Extraction;

/// <summary>
/// J-11, end to end on a real store: a corrected name renames what it named. Found live: "it's Priya, not Pruya" closed
/// the old naming fact but left "Pruya" as a second person, with "Pruya lives in Lisbon" still on it. The extractors are
/// scripted per message, so no model decides whether a correction happened.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class RenameOnCorrectedNameIntegrationTests : IAsyncLifetime
{
    private const string Owner = "rename-probe-priya";
    private const string OtherOwner = "rename-probe-bob";
    private readonly Neo4jIntegrationFixture _fixture;

    public RenameOnCorrectedNameIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>What each message yields: the extraction a model would return for it.</summary>
    private static readonly Dictionary<string, (ExtractedEntity[] Entities, ExtractedFact[] Facts)> Script = new()
    {
        ["Hi, I'm Pruya. I live in Lisbon."] = (
            [new() { Name = "Pruya", Type = "PERSON", Confidence = 0.95 }, new() { Name = "Lisbon", Type = "LOCATION", Confidence = 0.95 }],
            [new() { Subject = "user", Predicate = "is named", Object = "Pruya", Confidence = 0.95 },
             new() { Subject = "user", Predicate = "lives in", Object = "Lisbon", Confidence = 0.95 }]),
        ["Sorry, you got my name wrong: it's Priya, not Pruya."] = (
            [new() { Name = "Priya", Type = "PERSON", Confidence = 0.95 }],
            [new() { Subject = "user", Predicate = "is named", Object = "Priya", Confidence = 0.95, Replaces = "Pruya" }]),
        ["I just adopted a cat called Missou."] = (
            [new() { Name = "Missou", Type = "ANIMAL", Confidence = 0.95 }],
            [new() { Subject = "cat", Predicate = "is called", Object = "Missou", Confidence = 0.95 },
             new() { Subject = "Missou", Predicate = "likes", Object = "sleeping on the sofa", Confidence = 0.9 }]),
        ["No, the cat's name is Miso."] = (
            [new() { Name = "Miso", Type = "ANIMAL", Confidence = 0.95 }],
            [new() { Subject = "cat", Predicate = "is called", Object = "Miso", Confidence = 0.95, Replaces = "Missou" }]),
        ["My dog is called Rex, and I walk Rex every morning."] = (
            [new() { Name = "Rex", Type = "ANIMAL", Confidence = 0.95 }, new() { Name = "Priya", Type = "PERSON", Confidence = 0.95 }],
            [new() { Subject = "dog", Predicate = "is called", Object = "Rex", Confidence = 0.95 },
             new() { Subject = "Priya", Predicate = "walks", Object = "Rex", Confidence = 0.9 }]),
        ["Sorry, the dog is Max, not Rex."] = (
            [new() { Name = "Max", Type = "ANIMAL", Confidence = 0.95 }],
            [new() { Subject = "dog", Predicate = "is called", Object = "Max", Confidence = 0.95, Replaces = "Rex" }]),
        ["Bob walks a dog called Rex too."] = (
            [new() { Name = "Rex", Type = "ANIMAL", Confidence = 0.95 }],
            [new() { Subject = "Bob", Predicate = "walks", Object = "Rex", Confidence = 0.9 }]),
    };

    [Fact]
    public async Task Correcting_your_own_name_renames_you_instead_of_adding_a_second_person()
    {
        await using var provider = Build(rename: true);
        await SayAsync(provider, "Hi, I'm Pruya. I live in Lisbon.");
        await SayAsync(provider, "Sorry, you got my name wrong: it's Priya, not Pruya.");

        using var services = provider.CreateScope();
        var (entities, facts) = Repositories(services.ServiceProvider);
        var scope = MemoryScope.For(Owner, includeShared: false);

        var priya = await entities.FindLiveByNameAsync("Priya", null, scope);
        priya.Should().NotBeNull();
        priya!.Aliases.Should().Contain("Pruya", "the old name is still recognised, if said or misheard again");
        (await entities.FindLiveByNameAsync("Pruya", null, scope))!.EntityId.Should().Be(priya.EntityId,
            "\"Pruya\" now finds Priya (by alias), not a second person");

        (await facts.FindByTripleAsync("Priya", "lives in", "Lisbon", scope)).Should().NotBeNull()
            .And.Match<Fact>(f => f.InvalidatedAtUtc == null, "what was said under the wrong name is now hers");
        (await facts.FindByTripleAsync("Pruya", "lives in", "Lisbon", scope))!.InvalidatedAtUtc.Should().NotBeNull(
            "the fact under the old name is superseded, kept as history");
    }

    [Fact]
    public async Task Correcting_a_pets_name_renames_the_pet_and_what_was_said_about_it()
    {
        await using var provider = Build(rename: true);
        await SayAsync(provider, "I just adopted a cat called Missou.");
        await SayAsync(provider, "No, the cat's name is Miso.");

        using var services = provider.CreateScope();
        var (entities, facts) = Repositories(services.ServiceProvider);
        var scope = MemoryScope.For(Owner, includeShared: false);

        var miso = await entities.FindLiveByNameAsync("Miso", null, scope);
        miso!.Aliases.Should().Contain("Missou");
        (await facts.FindByTripleAsync("Miso", "likes", "sleeping on the sofa", scope))!.InvalidatedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task When_resolution_already_filed_the_new_name_under_the_old_entity_it_is_renamed_in_place()
    {
        await using var provider = Build(rename: true);
        await SayAsync(provider, "I just adopted a cat called Missou.");
        // Entity resolution can attach the corrected name to the existing entity as an alias (a close embedding):
        // then "Miso" finds "Missou" itself, and there is no second entity to merge.
        await using (var session = _fixture.Driver.AsyncSession())
            await session.RunAsync("MATCH (e:Entity {name: 'Missou', owner_id: $owner}) SET e.aliases = ['Miso']", new { owner = Owner });

        await SayAsync(provider, "No, the cat's name is Miso.");

        using var services = provider.CreateScope();
        var (entities, _) = Repositories(services.ServiceProvider);
        var cat = await entities.FindLiveByNameAsync("Miso", null, MemoryScope.For(Owner, includeShared: false));
        cat!.Name.Should().Be("Miso", "renamed in place: the corrected name is its own");
        cat.Aliases.Should().Contain("Missou").And.NotContain("Miso");
    }

    /// <summary>
    /// K-4 (38.2): a fact that names the old name as its object follows the rename too, closed and restated (never
    /// edited), and is linked to the renamed entity; another owner's "Rex" is another dog and stays as it was.
    /// </summary>
    [Fact]
    public async Task Facts_naming_the_old_name_as_their_object_follow_and_another_owners_stay()
    {
        await using var provider = Build(rename: true, link: true);
        await SayAsync(provider, "Bob walks a dog called Rex too.", OtherOwner);
        await SayAsync(provider, "My dog is called Rex, and I walk Rex every morning.");
        await SayAsync(provider, "Sorry, the dog is Max, not Rex.");

        using var services = provider.CreateScope();
        var (_, facts) = Repositories(services.ServiceProvider);
        var scope = MemoryScope.For(Owner, includeShared: false);

        var walks = await facts.FindByTripleAsync("Priya", "walks", "Max", scope);
        walks.Should().NotBeNull().And.Match<Fact>(f => f.InvalidatedAtUtc == null, "the fact naming the dog now names it rightly");
        (await facts.FindByTripleAsync("Priya", "walks", "Rex", scope))!.InvalidatedAtUtc.Should().NotBeNull(
            "the original is closed, kept as history");
        (await facts.FindByTripleAsync("Bob", "walks", "Rex", MemoryScope.For(OtherOwner, includeShared: false)))!
            .InvalidatedAtUtc.Should().BeNull("another owner's Rex is another dog");

        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(
            "MATCH (:Fact {id: $id})-[:ABOUT]->(e:Entity) RETURN collect(e.name) AS names", new { id = walks!.FactId });
        var names = ValueExtensions.As<List<string>>((await cursor.SingleAsync())["names"]);
        names.Should().BeEquivalentTo(["Priya", "Max"], "a restated fact is linked as a new one would be");
    }

    [Fact]
    public async Task Off_by_default_the_old_name_stays_beside_the_new_one()
    {
        await using var provider = Build(rename: false);
        await SayAsync(provider, "Hi, I'm Pruya. I live in Lisbon.");
        await SayAsync(provider, "Sorry, you got my name wrong: it's Priya, not Pruya.");

        using var services = provider.CreateScope();
        var (entities, facts) = Repositories(services.ServiceProvider);
        var scope = MemoryScope.For(Owner, includeShared: false);

        (await entities.FindLiveByNameAsync("Pruya", null, scope))!.Name.Should().Be("Pruya");
        (await facts.FindByTripleAsync("Pruya", "lives in", "Lisbon", scope))!.InvalidatedAtUtc.Should().BeNull();
    }

    private static (IEntityRepository Entities, IFactRepository Facts) Repositories(IServiceProvider provider) =>
        (provider.GetRequiredService<IEntityRepository>(), provider.GetRequiredService<IFactRepository>());

    private static async Task SayAsync(ServiceProvider provider, string text, string owner = Owner)
    {
        using var scope = provider.CreateScope();
        var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
        await shortTerm.AddConversationAsync($"conv-rename-{owner}", $"session-rename-{owner}", userId: owner);
        var message = await shortTerm.AddMessageAsync(new Message
        {
            MessageId = $"m-{Guid.NewGuid():N}",
            ConversationId = $"conv-rename-{owner}",
            SessionId = $"session-rename-{owner}",
            Role = "user",
            Content = text,
            TimestampUtc = DateTimeOffset.UtcNow,
        });
        await pipeline.ExtractAsync(new ExtractionRequest { SessionId = $"session-rename-{owner}", UserId = owner, Messages = [message] });
    }

    private ServiceProvider Build(bool rename, bool link = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEntityExtractor, ScriptedEntities>();
        services.AddSingleton<IFactExtractor, ScriptedFacts>();
        services.AddNeo4jAgentMemory(
            new MemoryOptions { Extraction = { SupersedeReplacedFacts = true, RenameOnCorrectedName = rename, LinkFactsToEntities = link } },
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
        return services.BuildServiceProvider(validateScopes: true);
    }

    private sealed class ScriptedEntities : IEntityExtractor
    {
        public Task<IReadOnlyList<ExtractedEntity>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedEntity>>([.. messages.SelectMany(m => Script[m.Content].Entities)]);
    }

    private sealed class ScriptedFacts : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>([.. messages.SelectMany(m => Script[m.Content].Facts)]);
    }
}
