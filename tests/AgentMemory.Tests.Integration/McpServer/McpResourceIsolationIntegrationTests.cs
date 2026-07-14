using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.McpServer.Resources;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.McpServer;

/// <summary>
/// Live-Neo4j owner-isolation for the MCP read resources that hand-roll their own Cypher via
/// <see cref="AgentMemory.Abstractions.Services.IGraphQueryService"/> (gap-1/gap-4). These resources do
/// NOT delegate to the owner-scoped repositories, so their scoping must be proven end-to-end against a
/// real graph: an owner-scoped read must return the owner's + shared rows and never another owner's.
/// <see cref="ContextResource"/> is covered separately below: it takes <see cref="IMemoryContextAssembler"/>
/// rather than <see cref="AgentMemory.Abstractions.Services.IGraphQueryService"/>, so its tests build a
/// real DI container (closes tests/README.md's "MCP ContextResource" gap).
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class McpResourceIsolationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jGraphQueryService _graph;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jPreferenceRepository _prefs;
    private readonly StubEmbeddingGenerator _embedder;
    private ServiceProvider _provider = null!;

    public McpResourceIsolationIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _graph = new Neo4jGraphQueryService(fixture.TransactionRunner, NullLogger<Neo4jGraphQueryService>.Instance);
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _prefs = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
        _embedder = new StubEmbeddingGenerator(NullLogger<StubEmbeddingGenerator>.Instance, Neo4jIntegrationFixture.TestEmbeddingDimensions);
    }

    public Task InitializeAsync()
    {
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
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(sp =>
            new StubEmbeddingGenerator(
                sp.GetRequiredService<ILogger<StubEmbeddingGenerator>>(),
                Neo4jIntegrationFixture.TestEmbeddingDimensions));
        _provider = services.BuildServiceProvider(validateScopes: true);

        return _fixture.CleanDatabaseAsync();
    }

    public async Task DisposeAsync() => await _provider.DisposeAsync();

    [Fact]
    public async Task EntityListResource_ScopedToOwner_ExcludesOtherOwners()
    {
        await _entities.UpsertAsync(new Entity { EntityId = $"e-{Guid.NewGuid():N}", Name = "AliceCo", Type = "Organization", OwnerId = "alice", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _entities.UpsertAsync(new Entity { EntityId = $"e-{Guid.NewGuid():N}", Name = "BobCo", Type = "Organization", OwnerId = "bob", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _entities.UpsertAsync(new Entity { EntityId = $"e-{Guid.NewGuid():N}", Name = "SharedCo", Type = "Organization", OwnerId = null, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        var names = NamesFrom(await EntityListResource.GetEntities(_graph, userId: "alice"), "entities", "name");

        names.Should().Contain("AliceCo").And.Contain("SharedCo").And.NotContain("BobCo");
    }

    [Fact]
    public async Task EntityListResource_Unscoped_ReturnsAllOwners()
    {
        await _entities.UpsertAsync(new Entity { EntityId = $"e-{Guid.NewGuid():N}", Name = "AliceCo", Type = "Organization", OwnerId = "alice", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _entities.UpsertAsync(new Entity { EntityId = $"e-{Guid.NewGuid():N}", Name = "BobCo", Type = "Organization", OwnerId = "bob", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        var names = NamesFrom(await EntityListResource.GetEntities(_graph), "entities", "name");

        names.Should().Contain("AliceCo").And.Contain("BobCo");
    }

    [Fact]
    public async Task PreferenceListResource_ScopedToOwner_ExcludesOtherOwners()
    {
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "alice dark mode", OwnerId = "alice", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "bob light mode", OwnerId = "bob", Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });
        await _prefs.UpsertAsync(new Preference { PreferenceId = $"p-{Guid.NewGuid():N}", Category = "style", PreferenceText = "shared palette", OwnerId = null, Confidence = 0.9, CreatedAtUtc = DateTimeOffset.UtcNow });

        var texts = NamesFrom(await PreferenceListResource.GetPreferences(_graph, userId: "alice"), "preferences", "preference");

        texts.Should().Contain("alice dark mode").And.Contain("shared palette").And.NotContain("bob light mode");
    }

    [Fact]
    public async Task ConversationListResource_ScopedToUser_ExcludesOtherUsers()
    {
        await SeedConversationAsync("conv-alice", "alice");
        await SeedConversationAsync("conv-bob", "bob");
        await SeedConversationAsync("conv-shared", null);

        var ids = NamesFrom(await ConversationListResource.GetConversations(_graph, userId: "alice"), "conversations", "id");

        ids.Should().Contain("conv-alice").And.Contain("conv-shared").And.NotContain("conv-bob");
    }

    [Fact]
    public async Task ContextResource_ScopedToOwner_ExcludesOtherOwners()
    {
        await SeedEntityAsync("AliceCo", ownerId: "alice");
        await SeedEntityAsync("BobCo", ownerId: "bob");
        await SeedEntityAsync("SharedCo", ownerId: null);

        var names = await GetContextEntityNamesAsync(userId: "alice");

        names.Should().Contain("AliceCo").And.Contain("SharedCo").And.NotContain("BobCo");
    }

    [Fact]
    public async Task ContextResource_ScopedToOtherOwner_ExcludesFirstOwner()
    {
        await SeedEntityAsync("AliceCo", ownerId: "alice");
        await SeedEntityAsync("BobCo", ownerId: "bob");
        await SeedEntityAsync("SharedCo", ownerId: null);

        var names = await GetContextEntityNamesAsync(userId: "bob");

        names.Should().Contain("BobCo").And.Contain("SharedCo").And.NotContain("AliceCo");
    }

    [Fact]
    public async Task ContextResource_Unscoped_ReturnsAllOwners()
    {
        // Explicit regression test documenting current behavior: ContextResource's userId is optional,
        // and an omitted/null value is unscoped (global), not "no access" -- matching every other MCP
        // resource in this file and the null-MemoryScope semantic documented on MemoryScope itself. This
        // makes that behavior visible rather than leaving it implicit (tests/README.md gap).
        await SeedEntityAsync("AliceCo", ownerId: "alice");
        await SeedEntityAsync("BobCo", ownerId: "bob");

        var names = await GetContextEntityNamesAsync(userId: null);

        names.Should().Contain("AliceCo").And.Contain("BobCo");
    }

    // ── Helpers ──

    // All seeded entities embed from this same marker text (not their own Name) and the query below
    // embeds the identical marker -- so every candidate scores a perfect 1.0 match regardless of its
    // actual name, matching OwnerScopeIsolationIntegrationTests' "identical embeddings" convention. Only
    // the owner WHERE-filter can then determine which of them come back.
    private const string EmbeddingMarker = "context-resource-isolation-test";

    private async Task SeedEntityAsync(string name, string? ownerId)
    {
        var embedding = await EmbedAsync(EmbeddingMarker);
        await _entities.UpsertAsync(new Entity
        {
            EntityId = $"e-{Guid.NewGuid():N}",
            Name = name,
            Type = "Organization",
            OwnerId = ownerId,
            Confidence = 0.9,
            Embedding = embedding,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        });
    }

    private async Task<List<string?>> GetContextEntityNamesAsync(string? userId)
    {
        using var scope = _provider.CreateScope();
        var assembler = scope.ServiceProvider.GetRequiredService<IMemoryContextAssembler>();

        var json = await ContextResource.GetContext(assembler, session_id: "ctx-session", query: EmbeddingMarker, userId: userId);
        return NamesFrom(json, "entities", "name");
    }

    private async Task<float[]> EmbedAsync(string text)
    {
        var generated = await _embedder.GenerateAsync([text]);
        return generated[0].Vector.ToArray();
    }

    private Task SeedConversationAsync(string id, string? userId) =>
        _fixture.TransactionRunner.WriteAsync(async runner =>
        {
            await runner.RunAsync(
                "CREATE (c:Conversation {id: $id, session_id: $id, user_id: $userId, created_at: datetime()})",
                new Dictionary<string, object?> { ["id"] = id, ["userId"] = userId });
        });

    private static List<string?> NamesFrom(string json, string arrayProp, string itemProp)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty(arrayProp).EnumerateArray()
            .Select(e => e.GetProperty(itemProp).GetString())
            .ToList();
    }
}
