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

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// G5 (PLAN 40.49), on a real store: why a fact was not recalled, by the gate that kept it out. A planted fact below the
/// similarity floor is reported with its score; a closed one with what replaced it; another owner's as theirs.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class WhyNotIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider? _provider;

    public WhyNotIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
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

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task Each_fact_is_explained_by_the_gate_that_kept_it_out()
    {
        var bilbao = await SayAsync("ana", "I live in Bilbao.");
        var madrid = await SayAsync("ana", "I moved to Madrid.");
        var hiking = await SayAsync("ana", "I like hiking.");
        var porto = await SayAsync("bob", "I live in Porto.");

        (await WhyNotAsync(bilbao, floor: 0, cap: 10)).Should().Match<MemoryWhyNot>(w =>
            w.Gate == MemoryWhyNot.Closed && w.RelatedId == madrid, "closed by the move, which replaced it");
        (await WhyNotAsync(porto, floor: 0, cap: 10)).Gate.Should().Be(MemoryWhyNot.Owner);
        (await WhyNotAsync("no-such-fact", floor: 0, cap: 10)).Gate.Should().Be(MemoryWhyNot.NotFound);

        var belowFloor = await WhyNotAsync(madrid, floor: 0.999, cap: 10);
        belowFloor.Gate.Should().Be(MemoryWhyNot.Similarity, "a floor nothing reaches keeps it out");
        belowFloor.Score.Should().BeLessThan(0.999);
        belowFloor.Floor.Should().Be(0.999);

        (await WhyNotAsync(madrid, floor: 0, cap: 10)).Gate.Should().Be(MemoryWhyNot.Recalled);
        var oneSlot = new[] { await WhyNotAsync(madrid, floor: 0, cap: 1), await WhyNotAsync(hiking, floor: 0, cap: 1) };
        oneSlot.Select(w => w.Gate).Should().BeEquivalentTo([MemoryWhyNot.Recalled, MemoryWhyNot.Rank],
            "one slot: one fact is recalled, the other is outranked");
    }

    private async Task<MemoryWhyNot> WhyNotAsync(string id, double floor, int cap, MemoryItemKind kind = MemoryItemKind.Fact)
    {
        using var scope = _provider!.CreateScope();
        var explainer = scope.ServiceProvider.GetRequiredService<IMemoryRecallExplainer>();
        return await explainer.WhyNotAsync(new RecallRequest
        {
            SessionId = "ask-ana", UserId = "ana", Query = "What do you know about where I live and what I like?",
            Options = new RecallOptions
            {
                MaxRecentMessages = 0, MaxRelevantMessages = 0, MaxTraces = 0,
                MaxFacts = cap, MaxEntities = cap, MaxPreferences = cap, MinSimilarityScore = floor,
            },
        }, kind, id);
    }

    /// <summary>The same gates for an entity (a merge names what it lives on in) and a preference.</summary>
    [Fact]
    public async Task Entities_and_preferences_are_explained_by_the_same_gates()
    {
        using var scope = _provider!.CreateScope();
        var entities = scope.ServiceProvider.GetRequiredService<AgentMemory.Abstractions.Repositories.IEntityRepository>();
        var preferences = scope.ServiceProvider.GetRequiredService<AgentMemory.Abstractions.Repositories.IPreferenceRepository>();
        var embed = scope.ServiceProvider.GetRequiredService<IEmbeddingOrchestrator>();
        async Task<Entity> Person(string name, string owner) => await entities.UpsertAsync(new Entity
        {
            EntityId = Guid.NewGuid().ToString("N"), Name = name, Type = "PERSON", Confidence = 0.9, OwnerId = owner,
            CreatedAtUtc = DateTimeOffset.UtcNow, Embedding = await embed.EmbedAsync(name),
        });
        var pruya = await Person("Pruya", "ana");
        var priya = await Person("Priya", "ana");
        var bobs = await Person("Dmitri", "bob");
        (await entities.MergeEntitiesAsync(pruya.EntityId, priya.EntityId, MemoryScope.For("ana", includeShared: false))).Should().BeTrue();
        await entities.InvalidateAsync(pruya.EntityId, MemoryScope.For("ana", includeShared: false));

        (await WhyNotAsync(pruya.EntityId, floor: 0, cap: 10, MemoryItemKind.Entity)).Should().Match<MemoryWhyNot>(w =>
            w.Gate == MemoryWhyNot.Merged && w.RelatedId == priya.EntityId);
        (await WhyNotAsync(bobs.EntityId, floor: 0, cap: 10, MemoryItemKind.Entity)).Gate.Should().Be(MemoryWhyNot.Owner);
        // A direct merge leaves the survivor without an embedding until the backfill runs (the rename path embeds it).
        (await WhyNotAsync(priya.EntityId, floor: 0, cap: 10, MemoryItemKind.Entity)).Gate.Should().Be(MemoryWhyNot.NotEmbedded);
        await scope.ServiceProvider.GetRequiredService<IMemoryService>().GenerateEmbeddingsBatchAsync(MemoryNodeKind.Entity, 50);
        (await WhyNotAsync(priya.EntityId, floor: 0, cap: 10, MemoryItemKind.Entity)).Gate.Should().Be(MemoryWhyNot.Recalled);

        var tea = await preferences.UpsertAsync(new Preference
        {
            PreferenceId = Guid.NewGuid().ToString("N"), Category = "drinks", PreferenceText = "Prefers green tea", Confidence = 0.9,
            OwnerId = "ana", CreatedAtUtc = DateTimeOffset.UtcNow, Embedding = await embed.EmbedAsync("Prefers green tea"),
        });
        (await WhyNotAsync(tea.PreferenceId, floor: 0, cap: 10, MemoryItemKind.Preference)).Gate.Should().Be(MemoryWhyNot.Recalled);
        await preferences.InvalidateAsync(tea.PreferenceId, MemoryScope.For("ana", includeShared: false));
        (await WhyNotAsync(tea.PreferenceId, floor: 0, cap: 10, MemoryItemKind.Preference)).Gate.Should().Be(MemoryWhyNot.Invalidated);
    }

    /// <summary>Says <paramref name="text"/> as <paramref name="owner"/> and returns the id of the fact it wrote (G4's outcome).</summary>
    private async Task<string> SayAsync(string owner, string text)
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
        var result = await pipeline.ExtractAsync(new ExtractionRequest { SessionId = $"s-{owner}", UserId = owner, Messages = [message] });
        return result.Outcomes.Single(o => o.Kind == MemoryItemKind.Fact && o.Status == IngestionItemStatus.Succeeded).PersistedId!;
    }

    private sealed class ScriptedFacts : IFactExtractor
    {
        public Task<IReadOnlyList<ExtractedFact>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedFact>>([.. messages.Select(m => m.Content switch
            {
                "I live in Bilbao." => Fact("lives in", "Bilbao"),
                "I moved to Madrid." => Fact("lives in", "Madrid"),
                "I like hiking." => Fact("likes", "hiking"),
                "I live in Porto." => Fact("lives in", "Porto"),
                _ => throw new InvalidOperationException($"No script for '{m.Content}'."),
            })]);

        private static ExtractedFact Fact(string predicate, string @object) =>
            new() { Subject = "user", Predicate = predicate, Object = @object, Confidence = 0.95 };
    }
}
