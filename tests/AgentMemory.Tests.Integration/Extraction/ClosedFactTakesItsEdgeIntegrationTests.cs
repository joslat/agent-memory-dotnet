using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using FluentAssertions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Extraction;

/// <summary>
/// 38.6, end to end on a real store: the ConversationalAgent sample as recorded (2026-10-01). After "I moved to Copenhagen,
/// and I'm doing the full marathon in May instead of the half in April" the facts were right, and recall still carried
/// "Lena —lives in→ Lyon", "Lena —training for→ half marathon" and "half marathon | takes place in | 2027-04": the agent
/// answered from them. The extractors are scripted per message, so no model decides what was said.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ClosedFactTakesItsEdgeIntegrationTests : IAsyncLifetime
{
    private const string Owner = "closed-fact-probe-lena";
    private const string First = "Hi, I'm Lena. I live in Lyon and I'm training for the half marathon in April.";
    private const string Second = "Big news: I moved to Copenhagen last week. And I'm doing the full marathon in May instead of the half in April.";
    private const string SameBreath = "I'm training for the full marathon now, not the half.";
    private static readonly DateTimeOffset April = new(2027, 4, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly Neo4jIntegrationFixture _fixture;

    public ClosedFactTakesItsEdgeIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>What each message yields: what GLM-5.3-Flash returned for it in the sample (the second names the speaker "user").</summary>
    private static readonly Dictionary<string, (ExtractedEntity[] Entities, ExtractedFact[] Facts, ExtractedRelationship[] Relationships)> Script = new()
    {
        [First] = (
            [
                new() { Name = "Lena", Type = "PERSON", Confidence = 0.95 },
                new() { Name = "Lyon", Type = "LOCATION", Confidence = 0.95 },
                new() { Name = "half marathon", Type = "EVENT", Confidence = 0.85 },
            ],
            [
                new() { Subject = "user", Predicate = "is named", Object = "Lena", Confidence = 0.95 },
                new() { Subject = "user", Predicate = "lives in", Object = "Lyon", Confidence = 0.95 },
                new() { Subject = "user", Predicate = "is training for", Object = "half marathon", Confidence = 0.9 },
                new() { Subject = "half marathon", Predicate = "takes place in", Object = "2027-04", Confidence = 0.9, ValidFrom = April },
            ],
            [
                new() { SourceEntity = "Lena", TargetEntity = "Lyon", RelationshipType = "lives in", Confidence = 0.9 },
                new() { SourceEntity = "Lena", TargetEntity = "half marathon", RelationshipType = "training for", Confidence = 0.9 },
            ]),
        [Second] = (
            [
                new() { Name = "Copenhagen", Type = "LOCATION", Confidence = 0.95 },
                new() { Name = "full marathon", Type = "EVENT", Confidence = 0.85 },
            ],
            [
                new() { Subject = "user", Predicate = "lives in", Object = "Copenhagen", Confidence = 0.95 },
                new() { Subject = "user", Predicate = "is doing", Object = "full marathon", Confidence = 0.9, Replaces = "the half in April" },
            ],
            [
                new() { SourceEntity = "user", TargetEntity = "Copenhagen", RelationshipType = "moved to", Confidence = 0.95 },
                new() { SourceEntity = "user", TargetEntity = "full marathon", RelationshipType = "is doing", Confidence = 0.9 },
            ]),
        [SameBreath] = (
            [new() { Name = "full marathon", Type = "EVENT", Confidence = 0.85 }],
            [new() { Subject = "user", Predicate = "is training for", Object = "full marathon", Confidence = 0.9, Replaces = "the half" }],
            [new() { SourceEntity = "user", TargetEntity = "full marathon", RelationshipType = "training for", Confidence = 0.9 }]),
    };

    [Fact]
    public async Task A_change_of_mind_leaves_nothing_live_that_still_says_the_old_thing()
    {
        await using var provider = Build();
        await SayAsync(provider, First);
        await SayAsync(provider, Second);

        using var scope = provider.CreateScope();
        var entities = scope.ServiceProvider.GetRequiredService<IEntityRepository>();
        var facts = scope.ServiceProvider.GetRequiredService<IFactRepository>();
        var relationships = scope.ServiceProvider.GetRequiredService<IRelationshipRepository>();
        var mine = MemoryScope.For(Owner, includeShared: false);

        var lena = await entities.FindLiveByNameAsync("Lena", null, mine);
        lena.Should().NotBeNull();
        var live = new List<string>();
        foreach (var edge in await relationships.GetBySourceEntityAsync(lena!.EntityId, mine))
        {
            if (edge.ValidUntil is { } until && until <= DateTimeOffset.UtcNow) continue;
            var target = await entities.GetByIdAsync(edge.TargetEntityId);
            live.Add($"{edge.RelationshipType.ToLowerInvariant().Replace('_', ' ')} → {target!.Name}");
        }
        live.Should().Contain(["moved to → Copenhagen", "is doing → full marathon"]);
        live.Should().NotContain(edge => edge.EndsWith("→ Lyon") || edge.EndsWith("→ half marathon"),
            "the facts they mirror were closed: a new home, and a changed plan");

        var stillSaid = (await facts.GetBySubjectAsync("half marathon", mine)).Where(fact => fact.InvalidatedAtUtc is null);
        stillSaid.Should().BeEmpty("the old race's date was the withdrawn plan's own date");
        (await facts.GetBySubjectAsync("Lena", mine)).Where(fact => fact.InvalidatedAtUtc is null)
            .Select(fact => $"{fact.Predicate} {fact.Object}")
            .Should().Contain(["lives in Copenhagen", "is doing full marathon"]).And.NotContain(["lives in Lyon", "is training for half marathon"]);
    }

    /// <summary>
    /// 38.6 review: a correction and what it corrects in ONE extraction (a session extracted at once). The closed fact's
    /// edge is written by the same extraction, after the facts: ended before it, it came back live.
    /// </summary>
    [Fact]
    public async Task A_change_of_mind_in_one_extraction_leaves_no_live_edge_to_the_old_plan()
    {
        await using var provider = Build();
        await SayAsync(provider, Owner, First, SameBreath);

        (await LiveEdgesAsync(provider, Owner)).Should().Contain("training for → full marathon")
            .And.NotContain(edge => edge.EndsWith("→ half marathon"), "the plan it mirrors was corrected in the same breath");
    }

    /// <summary>
    /// 38.6 review: a store without owners (single tenant). The shared-only read found no subject, and the facts keep
    /// "user" there while the edges hang off "Lena", so no closed fact's edge ever ended. (The second turn's own edges,
    /// from "user", are not stored without an owner: a limit of user-endpoint resolution, unchanged here.)
    /// </summary>
    [Fact]
    public async Task A_change_of_mind_without_an_owner_ends_the_edges_too()
    {
        var options = MemoryOptions.CreateConversational();
        options.Isolation.Mode = MemoryIsolationMode.SingleTenant;
        await using var provider = Build(options);
        await SayAsync(provider, null, First);
        await SayAsync(provider, null, Second);

        (await LiveEdgesAsync(provider, null)).Should().NotContain(edge => edge.EndsWith("→ Lyon") || edge.EndsWith("→ half marathon"));
        using var scope = provider.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IFactRepository>().GetBySubjectAsync("half marathon", MemoryScope.Global))
            .Where(fact => fact.InvalidatedAtUtc is null).Should().BeEmpty("the old race's date was the withdrawn plan's own");
    }

    private static async Task<List<string>> LiveEdgesAsync(ServiceProvider provider, string? owner)
    {
        using var scope = provider.CreateScope();
        var entities = scope.ServiceProvider.GetRequiredService<IEntityRepository>();
        var relationships = scope.ServiceProvider.GetRequiredService<IRelationshipRepository>();
        var read = owner is null ? MemoryScope.Global : MemoryScope.For(owner, includeShared: false);
        var lena = (await entities.GetByNameAsync("Lena", includeAliases: true, read)).Single();
        var live = new List<string>();
        foreach (var edge in await relationships.GetBySourceEntityAsync(lena.EntityId, read))
        {
            if (edge.ValidUntil is { } until && until <= DateTimeOffset.UtcNow) continue;
            var target = await entities.GetByIdAsync(edge.TargetEntityId);
            live.Add($"{edge.RelationshipType.ToLowerInvariant().Replace('_', ' ')} → {target!.Name}");
        }
        return live;
    }

    private static Task SayAsync(ServiceProvider provider, string text) => SayAsync(provider, Owner, text);

    private static async Task SayAsync(ServiceProvider provider, string? owner, params string[] texts)
    {
        using var scope = provider.CreateScope();
        var shortTerm = scope.ServiceProvider.GetRequiredService<IShortTermMemoryService>();
        var pipeline = scope.ServiceProvider.GetRequiredService<IMemoryExtractionPipeline>();
        await shortTerm.AddConversationAsync("conv-lena", "session-lena", userId: owner);
        var messages = new List<Message>();
        foreach (var text in texts)
        {
            messages.Add(await shortTerm.AddMessageAsync(new Message
            {
                MessageId = $"m-{Guid.NewGuid():N}",
                ConversationId = "conv-lena",
                SessionId = "session-lena",
                Role = "user",
                Content = text,
                TimestampUtc = DateTimeOffset.UtcNow,
            }));
        }
        await pipeline.ExtractAsync(new ExtractionRequest { SessionId = "session-lena", UserId = owner, Messages = messages });
    }

    private ServiceProvider Build(MemoryOptions? options = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IEntityExtractor, ScriptedEntities>();
        services.AddSingleton<IFactExtractor, ScriptedFacts>();
        services.AddSingleton<IRelationshipExtractor, ScriptedRelationships>();
        services.AddNeo4jAgentMemory(
            options ?? MemoryOptions.CreateConversational(),
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

    private sealed class ScriptedRelationships : IRelationshipExtractor
    {
        public Task<IReadOnlyList<ExtractedRelationship>> ExtractAsync(IReadOnlyList<Message> messages, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ExtractedRelationship>>([.. messages.SelectMany(m => Script[m.Content].Relationships)]);
    }
}
