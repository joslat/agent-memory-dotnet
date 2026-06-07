using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
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
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class McpResourceIsolationIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jGraphQueryService _graph;
    private readonly Neo4jEntityRepository _entities;
    private readonly Neo4jPreferenceRepository _prefs;

    public McpResourceIsolationIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _graph = new Neo4jGraphQueryService(fixture.TransactionRunner, NullLogger<Neo4jGraphQueryService>.Instance);
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
        _prefs = new Neo4jPreferenceRepository(fixture.TransactionRunner, NullLogger<Neo4jPreferenceRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

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

    // ── Helpers ──

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
