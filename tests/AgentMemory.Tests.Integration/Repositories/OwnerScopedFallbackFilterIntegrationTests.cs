using AgentMemory.Neo4j.Queries;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Neo4j.Driver;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// RVW-FALLBACK-FORGOTTEN: when an owner-scoped vector search and its widening return nothing, the preference and entity
/// paths score the owner's own rows directly. That scan filtered the owner only, so a forgotten, superseded or merged
/// item came back into recall, and a row embedded at another width made the whole search fail. It now applies what the
/// fact fallback and the indexed searches apply. Merging an entity sets merged_into but not invalidated_at, so the
/// indexed entity search drops merged entities too.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class OwnerScopedFallbackFilterIntegrationTests : IAsyncLifetime
{
    private static readonly double[] Query = [1, 0, 0, 0];
    private readonly Neo4jIntegrationFixture _fixture;

    public OwnerScopedFallbackFilterIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task The_preference_fallback_returns_only_live_preferences()
    {
        await RunAsync("""
            CREATE (:Preference {id: 'p-live', owner_id: 'ana', preference: 'tea', category: 'drink', embedding: [1.0, 0.0, 0.0, 0.0]})
            CREATE (:Preference {id: 'p-forgotten', owner_id: 'ana', preference: 'coffee', category: 'drink', embedding: [1.0, 0.0, 0.0, 0.0], invalidated_at: datetime()})
            CREATE (:Preference {id: 'p-merged', owner_id: 'ana', preference: 'green tea', category: 'drink', embedding: [1.0, 0.0, 0.0, 0.0], merged_into: 'p-live'})
            CREATE (:Preference {id: 'p-old-width', owner_id: 'ana', preference: 'water', category: 'drink', embedding: [1.0, 0.0, 0.0]})
            CREATE (:Preference {id: 'p-shared', preference: 'juice', category: 'drink', embedding: [1.0, 0.0, 0.0, 0.0]})
            CREATE (:Preference {id: 'p-bob', owner_id: 'bob', preference: 'milk', category: 'drink', embedding: [1.0, 0.0, 0.0, 0.0]})
            """);

        (await IdsAsync(PreferenceQueries.SearchByVectorOwnerScopedFallback(includeShared: false))).Should().Equal("p-live");
        (await IdsAsync(PreferenceQueries.SearchByVectorOwnerScopedFallback(includeShared: true))).Should().BeEquivalentTo("p-live", "p-shared");
    }

    [Fact]
    public async Task The_entity_fallback_returns_only_live_entities()
    {
        await RunAsync("""
            CREATE (:Entity {id: 'e-live', owner_id: 'ana', owner_key: 'ana', name: 'Bilbao', type: 'LOCATION', embedding: [1.0, 0.0, 0.0, 0.0]})
            CREATE (:Entity {id: 'e-forgotten', owner_id: 'ana', owner_key: 'ana', name: 'Porto', type: 'LOCATION', embedding: [1.0, 0.0, 0.0, 0.0], invalidated_at: datetime()})
            CREATE (:Entity {id: 'e-merged', owner_id: 'ana', owner_key: 'ana', name: 'Bilbo', type: 'LOCATION', embedding: [1.0, 0.0, 0.0, 0.0], merged_into: 'e-live'})
            CREATE (:Entity {id: 'e-old-width', owner_id: 'ana', owner_key: 'ana', name: 'Lisbon', type: 'LOCATION', embedding: [1.0, 0.0, 0.0]})
            CREATE (:Entity {id: 'e-shared', owner_key: '*', name: 'Madrid', type: 'LOCATION', embedding: [1.0, 0.0, 0.0, 0.0]})
            CREATE (:Entity {id: 'e-bob', owner_id: 'bob', owner_key: 'bob', name: 'Paris', type: 'LOCATION', embedding: [1.0, 0.0, 0.0, 0.0]})
            """);

        (await IdsAsync(EntityQueries.SearchByVectorOwnerScopedFallback(includeShared: false))).Should().Equal("e-live");
        (await IdsAsync(EntityQueries.SearchByVectorOwnerScopedFallback(includeShared: true))).Should().BeEquivalentTo("e-live", "e-shared");
    }

    [Fact]
    public async Task The_indexed_entity_search_drops_a_merged_entity()
    {
        await RunAsync("""
            CREATE (:Entity {id: 'e-live', owner_id: 'ana', owner_key: 'ana', name: 'Bilbao', type: 'LOCATION', embedding: [1.0, 0.0, 0.0, 0.0]})
            CREATE (:Entity {id: 'e-merged', owner_id: 'ana', owner_key: 'ana', name: 'Bilbo', type: 'LOCATION', embedding: [1.0, 0.0, 0.0, 0.0], merged_into: 'e-live'})
            """);

        (await IdsAsync(EntityQueries.SearchByVector(hasOwnerFilter: true, includeShared: false, topK: 10))).Should().Equal("e-live");
    }

    private async Task RunAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await (await session.RunAsync(cypher)).ConsumeAsync();
    }

    private async Task<IReadOnlyList<string>> IdsAsync(string cypher)
    {
        await using var session = _fixture.Driver.AsyncSession();
        var cursor = await session.RunAsync(cypher, new Dictionary<string, object?>
        {
            ["embedding"] = Query.ToList(),
            ["limit"] = 10,
            ["minScore"] = 0.5,
            ["ownerId"] = "ana",
            ["ownerKey"] = "ana",
        });
        var records = await cursor.ToListAsync();
        return [.. records.Select(r => ValueExtensions.As<string>(ValueExtensions.As<INode>(r["node"])["id"])).Order(StringComparer.Ordinal)];
    }
}
