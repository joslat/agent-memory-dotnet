using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using NSubstitute;

namespace AgentMemory.Tests.Integration.Services;

/// <summary>
/// Live-DB proof that the entity-resolution READ path is owner-scoped (R1). This was the one genuine
/// cross-owner leak the unscoped-reads audit found: <see cref="CompositeEntityResolver"/> fetched its
/// candidate set via the unscoped <c>GetByTypeAsync</c>, so one owner's incoming entity could exact/
/// fuzzy/semantic-match onto — and auto-merge into — another owner's private entity. With the fix, an
/// owner-scoped resolution must never resolve onto a foreign owner's entity.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class EntityResolutionOwnerScopeIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;

    public EntityResolutionOwnerScopeIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private CompositeEntityResolver CreateResolver(string newId)
    {
        var embed = Substitute.For<IEmbeddingOrchestrator>();
        embed.EmbedTextAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.1f, 0.2f, 0.3f, 0.4f });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var idGen = Substitute.For<IIdGenerator>();
        idGen.GenerateId().Returns(newId);

        return new CompositeEntityResolver(
            _entities, embed, Options.Create(new ExtractionOptions()),
            clock, idGen, NullLogger<CompositeEntityResolver>.Instance);
    }

    private static ExtractedEntity Extracted(string name, string type = "Organization") =>
        new() { Name = name, Type = type, Confidence = 1.0 };

    [Fact]
    public async Task Resolve_ScopedToOtherOwner_DoesNotResolveOntoForeignEntity_CreatesNew()
    {
        var bobId = $"bob-acme-{Guid.NewGuid():N}";
        await _entities.UpsertAsync(new Entity { EntityId = bobId, Name = "Acme Corp", Type = "Organization", OwnerId = "bob", Confidence = 1.0, CreatedAtUtc = DateTimeOffset.UtcNow });

        var newId = $"alice-new-{Guid.NewGuid():N}";
        var resolver = CreateResolver(newId);

        var result = await resolver.ResolveEntityAsync(Extracted("Acme Corp"), Array.Empty<string>(), MemoryScope.For("alice"));

        result.EntityId.Should().Be(newId, "alice's resolution must not see bob's private entity, so it creates a new one");
        result.EntityId.Should().NotBe(bobId);
    }

    [Fact]
    public async Task Resolve_ScopedToOwningUser_ResolvesOntoOwnEntity()
    {
        var bobId = $"bob-acme-{Guid.NewGuid():N}";
        await _entities.UpsertAsync(new Entity { EntityId = bobId, Name = "Acme Corp", Type = "Organization", OwnerId = "bob", Confidence = 1.0, CreatedAtUtc = DateTimeOffset.UtcNow });

        var resolver = CreateResolver($"unused-{Guid.NewGuid():N}");

        var result = await resolver.ResolveEntityAsync(Extracted("Acme Corp"), Array.Empty<string>(), MemoryScope.For("bob"));

        result.EntityId.Should().Be(bobId, "bob's own scope must resolve onto bob's existing entity");
    }

    [Fact]
    public async Task Resolve_ScopedToOwner_StillResolvesOntoSharedEntity()
    {
        var sharedId = $"shared-acme-{Guid.NewGuid():N}";
        await _entities.UpsertAsync(new Entity { EntityId = sharedId, Name = "Acme Corp", Type = "Organization", OwnerId = null, Confidence = 1.0, CreatedAtUtc = DateTimeOffset.UtcNow });

        var resolver = CreateResolver($"unused-{Guid.NewGuid():N}");

        var result = await resolver.ResolveEntityAsync(Extracted("Acme Corp"), Array.Empty<string>(), MemoryScope.For("alice"));

        result.EntityId.Should().Be(sharedId, "shared/global entities (owner_id IS NULL) remain matchable by any owner");
    }

    [Fact]
    public async Task Resolve_Unscoped_PreservesLegacyCrossOwnerMatch()
    {
        var bobId = $"bob-acme-{Guid.NewGuid():N}";
        await _entities.UpsertAsync(new Entity { EntityId = bobId, Name = "Acme Corp", Type = "Organization", OwnerId = "bob", Confidence = 1.0, CreatedAtUtc = DateTimeOffset.UtcNow });

        var resolver = CreateResolver($"unused-{Guid.NewGuid():N}");

        var result = await resolver.ResolveEntityAsync(Extracted("Acme Corp"), Array.Empty<string>()); // null scope

        result.EntityId.Should().Be(bobId, "a null scope reproduces pre-R1 unscoped resolution (single-tenant)");
    }
}
