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

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// J-7, on a real store: the people a recalled fact names bring their relationships, even when those people were not
/// recalled themselves. Found in simulated conversations: "what does my manager's husband do?" had "Daniel works as a
/// chef" and the edge "Priya Nair married to Daniel" in the store, and the edge never reached the agent. Entity recall
/// is switched off here, so the only way the edge can arrive is through the fact.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class RelationshipsThroughFactsIntegrationTests : IAsyncLifetime
{
    private const string Owner = "relations-probe";
    private readonly Neo4jIntegrationFixture _fixture;
    private ServiceProvider _provider = null!;

    public RelationshipsThroughFactsIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddNeo4jAgentMemory(new MemoryOptions(), configureNeo4j: o =>
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

        using var scope = _provider.CreateScope();
        var sp = scope.ServiceProvider;
        var now = DateTimeOffset.UtcNow;
        var embedder = sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>();
        var entities = sp.GetRequiredService<IEntityRepository>();
        // No embeddings on the people: vector search can never recall them directly.
        var priya = await entities.UpsertAsync(new Entity { EntityId = "priya", Name = "Priya Nair", Type = "PERSON", Confidence = 1, CreatedAtUtc = now, OwnerId = Owner });
        var daniel = await entities.UpsertAsync(new Entity { EntityId = "daniel", Name = "Daniel", Type = "PERSON", Confidence = 1, CreatedAtUtc = now, OwnerId = Owner });
        await sp.GetRequiredService<IRelationshipRepository>().UpsertAsync(new Relationship
        {
            RelationshipId = "priya-married-daniel", SourceEntityId = priya.EntityId, TargetEntityId = daniel.EntityId,
            RelationshipType = "married_to", Confidence = 0.9, CreatedAtUtc = now, OwnerId = Owner,
        });
        await sp.GetRequiredService<IFactRepository>().UpsertAsync(new Fact
        {
            FactId = "daniel-chef", Subject = "Daniel", Predicate = "works as", Object = "chef", Confidence = 0.9,
            CreatedAtUtc = now, OwnerId = Owner,
            Embedding = (await embedder.GenerateAsync(["Daniel works as chef"]))[0].Vector.ToArray(),
        });
    }

    public async Task DisposeAsync()
    {
        if (_provider is not null) await _provider.DisposeAsync();
    }

    [Fact]
    public async Task The_people_a_recalled_fact_names_bring_their_relationships()
    {
        using var scope = _provider.CreateScope();
        var memory = scope.ServiceProvider.GetRequiredService<IMemoryService>();

        var result = await memory.RecallAsync(new RecallRequest
        {
            SessionId = "s-relations",
            UserId = Owner,
            Query = "Daniel works as chef",
            Options = new RecallOptions
            {
                MaxRecentMessages = 0, MaxRelevantMessages = 0, MaxEntities = 0, MaxPreferences = 0, MaxTraces = 0,
                MaxFacts = 10, MaxRelationships = 5, MinSimilarityScore = 0,
            },
        }, CancellationToken.None);

        result.Context.RelevantFacts.Items.Should().Contain(f => f.Subject == "Daniel", "the fact is what was recalled");
        result.Context.RelevantEntities.Items.Should().BeEmpty("entity recall is off: the edge cannot come from it");
        result.Context.RelevantRelationships.Items.Should().ContainSingle(r =>
            r.SourceName == "Priya Nair" && r.TargetName == "Daniel" && r.Relationship.RelationshipType == "married_to");
    }
}
