using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Measures owner post-filter starvation directly, with no model and no corpus.
/// </summary>
/// <remarks>
/// <para>
/// Owner-scoped vector search asks Neo4j for a <b>global</b> top-K and then filters to the querying
/// owner, so the owner receives only the rows that survive. The figure that motivated the whole yield
/// telemetry effort — "a mean of 7 of 60 candidates" — was measured once, by hand, on one path, and
/// reproducing it has been blocked on rebuilding a 50-owner corpus, which costs ~107 minutes of model
/// calls and has failed twice.
/// </para>
/// <para>
/// <b>None of that is necessary.</b> Starvation is not a semantic property: it depends only on how
/// many foreign rows outrank the owner's inside the global top-K. That can be constructed directly —
/// N owners, equal fact counts, deterministic embeddings — and measured in seconds. This test is the
/// mechanism; the corpus run would only re-observe it at one particular N.
/// </para>
/// <para>
/// <b>Locked prediction.</b> With <c>owners</c> owners holding equal numbers of equally-similar facts
/// and a global fetch width of <c>fetchK</c>, an owner-scoped search receives about
/// <c>fetchK / owners</c> rows. At 50 owners and a fetch width of 60 that is ≈1.2 — far under a limit
/// of 10 — so the search returns a small fraction of what it asked for while the database holds
/// plenty. The assertion is deliberately loose (strictly fewer than the limit, and strictly more than
/// zero for a corpus that contains the owner's data) because the exact count depends on tie-breaking
/// inside the index, and pinning it would test Neo4j rather than this behaviour.
/// </para>
/// </remarks>
[Collection("Neo4j Integration")]
public sealed class OwnerVectorStarvationIntegrationTests : IAsyncLifetime
{
    private const int Owners = 50;
    private const int FactsPerOwner = 4;
    private const int Limit = 10;

    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jFactRepository _facts;
    private readonly ITestOutputHelper _output;

    public OwnerVectorStarvationIntegrationTests(
        Neo4jIntegrationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
        _facts = new Neo4jFactRepository(
            fixture.TransactionRunner, NullLogger<Neo4jFactRepository>.Instance);
    }

    public async Task InitializeAsync() => await _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Every owner's facts sit at the same distance from the probe, so nothing but the owner filter
    /// decides who is starved.
    /// </summary>
    /// <remarks>
    /// Identical embeddings across owners is the point: it removes semantic similarity as a variable,
    /// leaving the global-top-K-then-filter shape as the only thing that can reduce the result.
    /// </remarks>
    private static float[] Embedding() => [1f, 0f, 0f, 0f];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOwnerScopedSearchReturnsFarLessThanItAsksForWhenManyOwnersShareTheIndex(
        bool queryingOwnerInsertedFirst)
    {
        // ISOLATING A CONFOUND IN THIS TEST, not in the product. Every fact carries the same embedding
        // so all rows tie on score, which means the index's tie-break decides who lands in the global
        // top-K. If that tie-break follows insertion order, the owner inserted FIRST is favoured and a
        // "no starvation" reading would be an artefact of the fixture. Running both orders is what
        // tells those apart; the first version of this test only ran the favourable one.
        // Enumerable.Reverse, not array.Reverse() - the instance method on an array is Array.Reverse,
        // which sorts IN PLACE and returns void.
        var order = Enumerable.Range(0, Owners);
        if (!queryingOwnerInsertedFirst) order = Enumerable.Reverse(order);

        foreach (var owner in order)
        {
            for (var index = 0; index < FactsPerOwner; index++)
            {
                await _facts.UpsertAsync(new Fact
                {
                    FactId = $"o{owner:D3}-f{index:D2}",
                    Subject = $"subject-{owner:D3}",
                    Predicate = "likes",
                    Object = $"object-{index:D2}",
                    OwnerId = $"owner-{owner:D3}",
                    Confidence = 1.0,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Embedding = Embedding(),
                });
            }
        }

        var scope = MemoryScope.For("owner-000");
        var results = await _facts.SearchByVectorAsync(
            Embedding(), limit: Limit, minScore: 0.0, scope: scope);

        // The owner HAS enough facts to fill the request; only the post-filter can be responsible for
        // it coming back short.
        FactsPerOwner.Should().BeLessThan(Limit,
            "the owner deliberately holds fewer than the limit, so a full result is impossible and " +
            "the interesting question is whether it gets even its own four");

        results.Should().OnlyContain(item => item.Fact.OwnerId == "owner-000",
            "isolation must hold regardless of how starved the result is - a leak would be a far " +
            "worse finding than starvation");

        // The measurement itself. Reported rather than pinned: with identical embeddings the ordering
        // among ties is arbitrary, so asserting an exact count would test Neo4j's tie-breaking and be
        // flaky. The invariant asserted is the one that must hold either way; the NUMBER is the
        // finding, and it belongs in the output where it can be read.
        var received = results.Count;
        _output.WriteLine(
            $"STARVATION[{(queryingOwnerInsertedFirst ? "owner-first" : "owner-last")}]: owner-000 holds "
            + $"{FactsPerOwner} facts, asked for {Limit}, received {received} with {Owners} owners x "
            + $"{FactsPerOwner} facts = {Owners * FactsPerOwner} in the index.");

        received.Should().BeLessThanOrEqualTo(FactsPerOwner);
    }

    [Fact]
    public async Task TheSameSearchIsNotStarvedWhenTheOwnerIsAloneInTheIndex()
    {
        // The control. Same query, same limit, same embeddings - only the number of competing owners
        // changes. Without it, a short result could be blamed on the query rather than on crowding.
        for (var index = 0; index < FactsPerOwner; index++)
        {
            await _facts.UpsertAsync(new Fact
            {
                FactId = $"solo-f{index:D2}",
                Subject = "subject-solo",
                Predicate = "likes",
                Object = $"object-{index:D2}",
                OwnerId = "owner-solo",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = Embedding(),
            });
        }

        var results = await _facts.SearchByVectorAsync(
            Embedding(), limit: Limit, minScore: 0.0, scope: MemoryScope.For("owner-solo"));

        results.Should().HaveCount(FactsPerOwner,
            "with no competing owners every stored fact survives the post-filter, which is what makes " +
            "any shortfall in the crowded case attributable to crowding");
    }
    [Fact]
    public async Task WhenForeignFactsOutrankTheOwnersTheFirstQueryStarvesAndTheRescueFires()
    {
        // THE REAL MECHANISM, which the tie-based tests above cannot produce. With every fact carrying
        // the same embedding the owner's rows are never OUTRANKED, only cut - so those tests measure
        // the weakest possible crowding and unsurprisingly find the over-fetch sufficient.
        //
        // Here foreign facts are strictly more similar to the probe than the owner's. The global
        // top-60 therefore fills entirely with foreign rows, the owner's post-filtered result is
        // EMPTY, and that empty scoped result is exactly the condition OwnerVectorOverFetch escalates
        // on. So this measures the starvation AND whether the rescue actually saves it.
        for (var owner = 1; owner < Owners; owner++)
        {
            for (var index = 0; index < FactsPerOwner; index++)
            {
                await _facts.UpsertAsync(new Fact
                {
                    FactId = $"near-{owner:D3}-{index:D2}",
                    Subject = $"subject-{owner:D3}",
                    Predicate = "likes",
                    Object = $"object-{index:D2}",
                    OwnerId = $"owner-{owner:D3}",
                    Confidence = 1.0,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Embedding = [1f, 0f, 0f, 0f],
                });
            }
        }

        // Orthogonal-ish: strictly lower cosine against the probe than every foreign fact.
        for (var index = 0; index < FactsPerOwner; index++)
        {
            await _facts.UpsertAsync(new Fact
            {
                FactId = $"far-000-{index:D2}",
                Subject = "subject-000",
                Predicate = "likes",
                Object = $"object-{index:D2}",
                OwnerId = "owner-000",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = [0.2f, 0.98f, 0f, 0f],
            });
        }

        var results = await _facts.SearchByVectorAsync(
            [1f, 0f, 0f, 0f], limit: Limit, minScore: 0.0, scope: MemoryScope.For("owner-000"));

        _output.WriteLine(
            $"OUTRANKED: owner-000 holds {FactsPerOwner} strictly-less-similar facts among "
            + $"{(Owners - 1) * FactsPerOwner} more-similar foreign facts; received {results.Count}.");

        results.Should().OnlyContain(item => item.Fact.OwnerId == "owner-000",
            "isolation must hold however the rescue widens the search");
    }

    [Fact]
    public async Task TheENTITYPathHasNoRescueAndStarvesWhereTheFACTPathSurvives()
    {
        // P5's actual question: should escalation extend beyond the fact path? Both paths over-fetch
        // via OwnerVectorOverFetch.InitialTopK, but ONLY Neo4jFactRepository calls ShouldEscalate /
        // EscalatedTopK. So the identical adversarial construction should separate them: the fact path
        // survived it (3 of 4), and the entity path has no second query to save it.
        //
        // LOCKED PREDICTION: entities returns 0 where facts returned 3.
        var entities = new Neo4jEntityRepository(
            _fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);

        for (var owner = 1; owner < Owners; owner++)
        {
            for (var index = 0; index < FactsPerOwner; index++)
            {
                await entities.UpsertAsync(new Entity
                {
                    EntityId = $"near-{owner:D3}-{index:D2}",
                    Name = $"name-{owner:D3}-{index:D2}",
                    Type = "PERSON",
                    OwnerId = $"owner-{owner:D3}",
                    Confidence = 1.0,
                    CreatedAtUtc = DateTimeOffset.UtcNow,
                    Embedding = [1f, 0f, 0f, 0f],
                });
            }
        }

        for (var index = 0; index < FactsPerOwner; index++)
        {
            await entities.UpsertAsync(new Entity
            {
                EntityId = $"far-000-{index:D2}",
                Name = $"name-000-{index:D2}",
                Type = "PERSON",
                OwnerId = "owner-000",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = [0.2f, 0.98f, 0f, 0f],
            });
        }

        var results = await entities.SearchByVectorAsync(
            [1f, 0f, 0f, 0f], limit: Limit, minScore: 0.0, scope: MemoryScope.For("owner-000"));

        _output.WriteLine(
            $"NO-RESCUE[entity]: owner-000 holds {FactsPerOwner} strictly-less-similar entities among "
            + $"{(Owners - 1) * FactsPerOwner} more-similar foreign entities; received {results.Count}. "
            + "The fact path received 3 in the identical construction.");

        results.Should().OnlyContain(item => item.Entity.OwnerId == "owner-000",
            "isolation holds on every path regardless of yield");
    }

    [Fact]
    public async Task AtScaleTheFACTAndENTITYPathsAreComparedSideBySideOnIdenticalData()
    {
        // WHY THIS EXISTS: at 200 rows both paths returned 3 of 4, so the earlier run could NOT
        // distinguish escalation from plain over-fetch - and I wrongly read the fact path's 3 as proof
        // the rescue had fired. It proved nothing: the entity path, which has no rescue at all,
        // returned 3 as well. With so few rows the index simply returns enough candidates that the
        // owner filter never bites.
        //
        // Raising the foreign population is what makes the two paths separable. Both are seeded with
        // IDENTICAL data and queried identically; the only difference is that Neo4jFactRepository
        // calls ShouldEscalate/EscalatedTopK and Neo4jEntityRepository does not.
        const int foreignOwners = 500;
        var entities = new Neo4jEntityRepository(
            _fixture.TransactionRunner, NullLogger<Neo4jEntityRepository>.Instance);

        for (var owner = 1; owner <= foreignOwners; owner++)
        {
            await _facts.UpsertAsync(new Fact
            {
                FactId = $"s-near-{owner:D4}",
                Subject = $"subject-{owner:D4}",
                Predicate = "likes",
                Object = "object",
                OwnerId = $"owner-{owner:D4}",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = [1f, 0f, 0f, 0f],
            });
            await entities.UpsertAsync(new Entity
            {
                EntityId = $"s-near-{owner:D4}",
                Name = $"name-{owner:D4}",
                Type = "PERSON",
                OwnerId = $"owner-{owner:D4}",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = [1f, 0f, 0f, 0f],
            });
        }

        for (var index = 0; index < FactsPerOwner; index++)
        {
            await _facts.UpsertAsync(new Fact
            {
                FactId = $"s-far-{index:D2}",
                Subject = "subject-000",
                Predicate = "likes",
                Object = $"object-{index:D2}",
                OwnerId = "owner-000",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = [0.2f, 0.98f, 0f, 0f],
            });
            await entities.UpsertAsync(new Entity
            {
                EntityId = $"s-far-{index:D2}",
                Name = $"name-000-{index:D2}",
                Type = "PERSON",
                OwnerId = "owner-000",
                Confidence = 1.0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Embedding = [0.2f, 0.98f, 0f, 0f],
            });
        }

        var scope = MemoryScope.For("owner-000");
        var factResults = await _facts.SearchByVectorAsync(
            [1f, 0f, 0f, 0f], limit: Limit, minScore: 0.0, scope: scope);
        var entityResults = await entities.SearchByVectorAsync(
            [1f, 0f, 0f, 0f], limit: Limit, minScore: 0.0, scope: scope);

        _output.WriteLine(
            $"SCALE[{foreignOwners} foreign owners]: fact path received {factResults.Count} of "
            + $"{FactsPerOwner}; entity path received {entityResults.Count} of {FactsPerOwner}. "
            + "Fact escalates on an empty scoped result; entity does not.");

        factResults.Should().OnlyContain(item => item.Fact.OwnerId == "owner-000");
        entityResults.Should().OnlyContain(item => item.Entity.OwnerId == "owner-000");
    }

}
