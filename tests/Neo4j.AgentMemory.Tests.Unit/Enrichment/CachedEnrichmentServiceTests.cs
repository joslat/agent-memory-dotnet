using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Enrichment;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Enrichment;

public sealed class CachedEnrichmentServiceTests
{
    private static readonly EnrichmentResult SampleResult = new()
    {
        EntityName = "London",
        Description = "Capital of England",
        WikipediaUrl = "https://en.wikipedia.org/wiki/London"
    };

    private static (CachedEnrichmentService Sut, IEnrichmentService Inner) CreateSut(
        EnrichmentCacheOptions? opts = null)
    {
        var inner = Substitute.For<IEnrichmentService>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(opts ?? new EnrichmentCacheOptions());

        var sut = new CachedEnrichmentService(
            inner, cache, options, NullLogger<CachedEnrichmentService>.Instance);

        return (sut, inner);
    }

    [Fact]
    public async Task EnrichEntity_CacheMiss_DelegatesToInner()
    {
        var (sut, inner) = CreateSut();
        inner.EnrichEntityAsync("London", "PLACE", Arg.Any<CancellationToken>())
             .Returns(SampleResult);

        var result = await sut.EnrichEntityAsync("London", "PLACE");

        result.Should().BeEquivalentTo(SampleResult);
        await inner.Received(1).EnrichEntityAsync("London", "PLACE", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichEntity_CacheHit_ReturnsFromCacheWithoutCallingInner()
    {
        var (sut, inner) = CreateSut();
        inner.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(SampleResult);

        await sut.EnrichEntityAsync("London", "PLACE");
        await sut.EnrichEntityAsync("London", "PLACE");

        await inner.Received(1).EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichEntity_NullResult_NotCached()
    {
        var (sut, inner) = CreateSut();
        inner.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns((EnrichmentResult?)null);

        var r1 = await sut.EnrichEntityAsync("Unknown", "THING");
        var r2 = await sut.EnrichEntityAsync("Unknown", "THING");

        r1.Should().BeNull();
        r2.Should().BeNull();
        await inner.Received(2).EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichEntity_DifferentEntityTypes_CachedSeparately()
    {
        var (sut, inner) = CreateSut();
        inner.EnrichEntityAsync("London", Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(SampleResult);

        await sut.EnrichEntityAsync("London", "PLACE");
        await sut.EnrichEntityAsync("London", "PERSON");

        // two different keys → inner called twice
        await inner.Received(2).EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichEntity_KeyNormalization_CaseInsensitiveHit()
    {
        var (sut, inner) = CreateSut();
        inner.EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
             .Returns(SampleResult);

        await sut.EnrichEntityAsync("London", "PLACE");
        await sut.EnrichEntityAsync("LONDON", "place");

        // Both resolve to the same normalised key → inner called only once
        await inner.Received(1).EnrichEntityAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
