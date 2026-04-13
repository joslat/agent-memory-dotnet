using FluentAssertions;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Enrichment;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Enrichment;

public sealed class CachedGeocodingServiceTests
{
    private static readonly GeocodingResult SampleResult = new()
    {
        Latitude = 51.5074,
        Longitude = -0.1278,
        City = "London",
        Provider = "Nominatim"
    };

    private static (CachedGeocodingService Sut, IGeocodingService Inner, IMemoryCache Cache) CreateSut(
        EnrichmentCacheOptions? cacheOptions = null)
    {
        var inner = Substitute.For<IGeocodingService>();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var options = Options.Create(cacheOptions ?? new EnrichmentCacheOptions());

        var sut = new CachedGeocodingService(
            inner, cache, options, NullLogger<CachedGeocodingService>.Instance);

        return (sut, inner, cache);
    }

    [Fact]
    public async Task GetCached_Miss_DelegatesToInner()
    {
        var (sut, inner, _) = CreateSut();
        inner.GeocodeAsync("London", Arg.Any<CancellationToken>())
            .Returns(SampleResult);

        var result = await sut.GeocodeAsync("London");

        result.Should().BeEquivalentTo(SampleResult);
        await inner.Received(1).GeocodeAsync("London", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCached_Hit_ReturnsFromCache()
    {
        var (sut, inner, _) = CreateSut();
        inner.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SampleResult);

        await sut.GeocodeAsync("London");
        await sut.GeocodeAsync("London");

        // Inner should only be called once — second call hits cache
        await inner.Received(1).GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCached_InnerError_NotCached()
    {
        var (sut, inner, _) = CreateSut();
        inner.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((GeocodingResult?)null);

        var r1 = await sut.GeocodeAsync("UnknownPlace");
        var r2 = await sut.GeocodeAsync("UnknownPlace");

        r1.Should().BeNull();
        r2.Should().BeNull();
        // Both calls must delegate — null result is not cached
        await inner.Received(2).GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetCached_TTL_Respected()
    {
        // Use a very short TTL
        var opts = new EnrichmentCacheOptions { GeocodingCacheDuration = TimeSpan.FromMilliseconds(50) };
        var (sut, inner, _) = CreateSut(opts);
        inner.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SampleResult);

        await sut.GeocodeAsync("London");
        await Task.Delay(100); // let the cache entry expire
        await sut.GeocodeAsync("London");

        // Both calls should have reached the inner service
        await inner.Received(2).GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
