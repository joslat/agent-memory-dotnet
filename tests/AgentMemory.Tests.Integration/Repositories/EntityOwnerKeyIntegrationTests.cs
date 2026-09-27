using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// 36.9 (G-17): entities carry <c>owner_key</c> (<c>"*"</c> when shared), so the shared half of entity resolution's
/// "own or shared" candidate read is an index seek instead of a scan of every entity of the type; an existing store
/// gets the key at bootstrap.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class EntityOwnerKeyIntegrationTests : IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 27, 0, 0, 0, TimeSpan.Zero);
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jEntityRepository _entities;

    public EntityOwnerKeyIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _entities = new Neo4jEntityRepository(fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private static Entity E(string id, string? owner) => new()
    {
        EntityId = id, Name = id, Type = "PERSON", Confidence = 1, CreatedAtUtc = T0, OwnerId = owner,
    };

    private async Task<string?> OwnerKeyOf(string id)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync("MATCH (e:Entity {id: $id}) RETURN e.owner_key AS k", new { id });
        return global::Neo4j.Driver.ValueExtensions.As<string?>((await cursor.SingleAsync())["k"]);
    }

    [Fact]
    public async Task Every_entity_write_path_keys_the_owner()
    {
        await _entities.UpsertAsync(E("single-own", "owner-a"));
        await _entities.UpsertAsync(E("single-shared", null));
        await _entities.UpsertBatchAsync([E("batch-own", "owner-a"), E("batch-shared", null)]);
        await _entities.UpsertFusedBatchAsync([E("fused-own", "owner-a"), E("fused-shared", null)]);

        foreach (var id in new[] { "single-own", "batch-own", "fused-own" })
            (await OwnerKeyOf(id)).Should().Be("owner-a", id);
        foreach (var id in new[] { "single-shared", "batch-shared", "fused-shared" })
            (await OwnerKeyOf(id)).Should().Be("*", id);
    }

    [Fact]
    public async Task Resolution_candidates_are_the_owners_and_the_shared_ones_and_never_anothers()
    {
        await _entities.UpsertAsync(E("mine", "owner-a"));
        await _entities.UpsertAsync(E("everyones", null));
        await _entities.UpsertAsync(E("theirs", "owner-b"));
        var scope = MemoryScope.For("owner-a", includeShared: true);

        (await _entities.GetByTypeAsync("PERSON", scope)).Select(e => e.EntityId).Should().BeEquivalentTo(["mine", "everyones"]);
        (await _entities.GetByTypeWithoutEmbeddingAsync("PERSON", scope)).Select(e => e.EntityId).Should().BeEquivalentTo(["mine", "everyones"]);
    }

    [Fact]
    public async Task The_shared_half_is_an_index_seek_on_the_owner_key()
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync("EXPLAIN " + AgentMemory.Neo4j.Queries.EntityQueries.GetByType(true, true),
            new { type = "PERSON", ownerId = "owner-a" });
        var summary = await cursor.ConsumeAsync();

        Describe(summary.Plan).Should().Contain("owner_key", "the shared half seeks entity_owner_key_idx");
    }

    private static string Describe(IPlan plan) =>
        plan.OperatorType + " " + string.Join(" ", plan.Arguments.Values) + " " + string.Join(" ", plan.Children.Select(Describe));

    [Fact]
    public async Task An_existing_store_gets_the_key_at_bootstrap_and_a_rewrite_heals_it()
    {
        await _entities.UpsertAsync(E("old-shared", null));
        await _entities.UpsertAsync(E("old-own", "owner-a"));
        await _entities.UpsertAsync(E("old-healed", "owner-a"));
        await using (var session = _fixture.Driver.AsyncSession())
            await session.RunAsync("MATCH (e:Entity) REMOVE e.owner_key");

        await _entities.UpsertAsync(E("old-healed", "owner-a"));
        (await OwnerKeyOf("old-healed")).Should().Be("owner-a", "a match on a legacy row gives it its key");

        var bootstrapper = new SchemaBootstrapper(_fixture.TransactionRunner,
            Options.Create(new Neo4jOptions { EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions }),
            NullLogger<SchemaBootstrapper>.Instance);
        (await bootstrapper.BackfillEntityOwnerKeysAsync(1)).Should().Be(2, "batches of one still reach every row");

        (await OwnerKeyOf("old-shared")).Should().Be("*");
        (await OwnerKeyOf("old-own")).Should().Be("owner-a");
        (await _entities.GetByTypeAsync("PERSON", MemoryScope.For("owner-a", includeShared: true)))
            .Select(e => e.EntityId).Should().Contain("old-shared");
    }
}
