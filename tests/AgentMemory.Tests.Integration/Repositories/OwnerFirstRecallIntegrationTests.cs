using System.Diagnostics;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// G-14, owner-first recall: a small owner is scored exactly and cannot be crowded out of the global index.
/// </summary>
/// <remarks>
/// The construction is <see cref="ShortRescueYieldArmIntegrationTests"/>': 49 foreign owners whose facts are MORE
/// aligned with the query than the querying owner's, so they fill the global top-K first (crowding is about rank,
/// not volume). Found live (F-13): “What does Dana do for work?” → “nothing stored” while it was stored.
/// </remarks>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class OwnerFirstRecallIntegrationTests : IAsyncLifetime
{
    private const int Owners = 50;
    private const int FactsPerOwner = 4;
    private const int Limit = 10;
    private static readonly float[] Query = [1f, 0f, 0f, 0f];

    private readonly Neo4jIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public OwnerFirstRecallIntegrationTests(Neo4jIntegrationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private Neo4jFactRepository Repository(int threshold) =>
        new(_fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance,
            memoryOptions: Options.Create(new MemoryOptions { OwnerFirstVectorThreshold = threshold }),
            ownerRowCounts: new OwnerRowCounts());

    private static Fact F(string id, string? owner, int index, float[] embedding) => new()
    {
        FactId = id, Subject = $"subject-{owner ?? "shared"}", Predicate = "likes", Object = $"object-{index:D2}",
        OwnerId = owner, Confidence = 1.0, CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10), Embedding = embedding,
    };

    private async Task SeedAsync(bool withShared = false)
    {
        var writer = Repository(0);
        for (var owner = 1; owner < Owners; owner++)
            for (var index = 0; index < FactsPerOwner; index++)
                await writer.UpsertAsync(F($"f-{owner:D3}-{index:D2}", $"owner-{owner:D3}", index, [1f, 0f, 0f, 0f]));
        // The querying owner: live, embedded, well above the floor, just less aligned: it loses every tie.
        for (var index = 0; index < FactsPerOwner; index++)
            await writer.UpsertAsync(F($"f-000-{index:D2}", "owner-000", index, [0.2f, 0.98f, 0f, 0f]));
        if (withShared)
            for (var index = 0; index < 2; index++)
                await writer.UpsertAsync(F($"shared-{index:D2}", null, index, [0.3f, 0.95f, 0f, 0f]));
    }

    [Fact]
    public async Task A_crowded_small_owner_gets_all_its_facts_back()
    {
        await SeedAsync();
        var scope = MemoryScope.For("owner-000", includeShared: false);

        var indexed = await Repository(0).SearchByVectorAsync(Query, Limit, 0.0, scope);
        var watch = Stopwatch.StartNew();
        var ownerFirst = await Repository(500).SearchByVectorAsync(Query, Limit, 0.0, scope);
        watch.Stop();
        _output.WriteLine($"OWNER-FIRST  index path: {indexed.Count}/{FactsPerOwner}   owner-first: {ownerFirst.Count}/{FactsPerOwner} in {watch.ElapsedMilliseconds} ms");

        // The starvation is 5.26's global index: 2026.x finds this owner through it, and there is nothing to rescue.
        if (await _fixture.ServerMajorAsync() < 2026)
            indexed.Count.Should().BeLessThan(FactsPerOwner, "void witness: the construction must starve the index path");
        ownerFirst.Should().HaveCount(FactsPerOwner);
        ownerFirst.Should().OnlyContain(r => r.Fact.OwnerId == "owner-000");
        ownerFirst.Select(r => r.Score).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task With_shared_included_the_owner_gets_its_own_and_the_shared_facts_and_nothing_foreign()
    {
        await SeedAsync(withShared: true);
        var scope = MemoryScope.For("owner-000", includeShared: true);

        var results = await Repository(500).SearchByVectorAsync(Query, Limit, 0.0, scope);

        results.Should().HaveCount(FactsPerOwner + 2);
        results.Should().OnlyContain(r => r.Fact.OwnerId == "owner-000" || r.Fact.OwnerId == null);
    }

    [Fact]
    public async Task An_owner_over_the_threshold_keeps_the_index_path()
    {
        await SeedAsync();
        var scope = MemoryScope.For("owner-000", includeShared: false);

        // Threshold 3 < the owner's 4 facts: the index path, starved exactly as before (on 5.26: 2026.x's index is not
        // starved by this construction, so there the path cannot be told apart by its yield).
        var results = await Repository(3).SearchByVectorAsync(Query, Limit, 0.0, scope);

        if (await _fixture.ServerMajorAsync() < 2026)
            results.Count.Should().BeLessThan(FactsPerOwner);
    }

    [Fact]
    public async Task The_as_of_path_is_owner_first_too()
    {
        await SeedAsync();
        var scope = MemoryScope.For("owner-000", includeShared: false);
        var now = DateTimeOffset.UtcNow;

        var indexed = await Repository(0).SearchByVectorAsOfAsync(Query, now, limit: Limit, minScore: 0.0, scope: scope);
        var ownerFirst = await Repository(500).SearchByVectorAsOfAsync(Query, now, limit: Limit, minScore: 0.0, scope: scope);

        indexed.Count.Should().BeLessThan(FactsPerOwner, "void witness for the as-of index path");
        ownerFirst.Should().HaveCount(FactsPerOwner);
    }

    [Fact]
    public async Task An_owners_derived_fact_without_an_owner_key_is_scanned()
    {
        // Derived facts carry owner_id but no owner_key (guard G2): the owner branch must seek owner_id.
        await SeedAsync();
        await using (var session = _fixture.Driver.AsyncSession())
            await session.RunAsync(
                "CREATE (:Fact {id: 'derived-000', subject: 'user', predicate: 'savings_total', object: '850', " +
                "owner_id: 'owner-000', fact_kind: 'derived', confidence: 1.0, created_at: datetime(), embedding: [0.2, 0.98, 0.0, 0.0]})");
        var scope = MemoryScope.For("owner-000", includeShared: false);

        var results = await Repository(500).SearchByVectorAsync(Query, Limit, 0.0, scope);

        results.Select(r => r.Fact.FactId).Should().Contain("derived-000");
    }

    [Fact]
    public async Task A_large_shared_corpus_keeps_the_owner_scan_and_adds_the_shared_rows_from_the_index()
    {
        // Threshold 3: a 2-fact owner is under it, 5 shared facts are over it, so the plan is ScanOwnerPlusIndex.
        await SeedAsync();
        var writer = Repository(0);
        for (var index = 0; index < 5; index++)
            await writer.UpsertAsync(F($"shared-big-{index:D2}", null, index, [1f, 0f, 0f, 0f]));
        for (var index = 0; index < 2; index++)
            await writer.UpsertAsync(F($"f-small-{index:D2}", "owner-small", index, [0.2f, 0.98f, 0f, 0f]));
        var scope = MemoryScope.For("owner-small", includeShared: true);

        var results = await Repository(3).SearchByVectorAsync(Query, Limit, 0.0, scope);

        results.Where(r => r.Fact.OwnerId == "owner-small").Should().HaveCount(2, "the owner is scanned, not crowded out");
        results.Where(r => r.Fact.OwnerId == null).Should().NotBeEmpty("the shared rows come from the index");
        results.Should().OnlyContain(r => r.Fact.OwnerId == "owner-small" || r.Fact.OwnerId == null);
        results.Select(r => r.Fact.FactId).Should().OnlyHaveUniqueItems();
        results.Select(r => r.Score).Should().BeInDescendingOrder();
    }

    [Fact]
    public async Task A_fact_of_another_dimension_is_skipped_not_thrown_on()
    {
        await SeedAsync();
        await Repository(0).UpsertAsync(F("f-000-odd", "owner-000", 99, [1f, 0f, 0f]));
        var scope = MemoryScope.For("owner-000", includeShared: false);

        var results = await Repository(500).SearchByVectorAsync(Query, Limit, 0.0, scope);

        results.Should().HaveCount(FactsPerOwner);
        results.Select(r => r.Fact.FactId).Should().NotContain("f-000-odd");
    }

    [Fact]
    public async Task The_largest_threshold_counts_without_overflowing()
    {
        // Review round 2: the capped count's LIMIT was threshold + 1, which wrapped negative at int.MaxValue.
        await SeedAsync();
        var scope = MemoryScope.For("owner-000", includeShared: true);

        var results = await Repository(int.MaxValue).SearchByVectorAsync(Query, Limit, 0.0, scope);

        results.Where(r => r.Fact.OwnerId == "owner-000").Should().HaveCount(FactsPerOwner);
    }
}
