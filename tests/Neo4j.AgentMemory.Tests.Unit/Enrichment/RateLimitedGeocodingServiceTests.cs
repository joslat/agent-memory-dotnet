using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Enrichment;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Enrichment;

public sealed class RateLimitedGeocodingServiceTests
{
    private static readonly GeocodingResult SampleResult = new()
    {
        Latitude = 51.5074,
        Longitude = -0.1278,
        Provider = "Nominatim"
    };

    private static RateLimitedGeocodingService CreateSut(
        IGeocodingService inner,
        int rateLimitPerSecond = 10)
    {
        var options = Options.Create(new GeocodingOptions { RateLimitPerSecond = rateLimitPerSecond });
        return new RateLimitedGeocodingService(
            inner, options, NullLogger<RateLimitedGeocodingService>.Instance);
    }

    [Fact]
    public async Task RateLimit_SingleRequest_PassesThrough()
    {
        var inner = Substitute.For<IGeocodingService>();
        inner.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SampleResult);

        using var sut = CreateSut(inner, rateLimitPerSecond: 10);

        var result = await sut.GeocodeAsync("London");

        result.Should().BeEquivalentTo(SampleResult);
        await inner.Received(1).GeocodeAsync("London", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RateLimit_BurstRequests_ThrottlesSecondRequest()
    {
        var inner = Substitute.For<IGeocodingService>();
        inner.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SampleResult);

        // 2 req/sec means minimum 500ms between requests
        using var sut = CreateSut(inner, rateLimitPerSecond: 2);

        var sw = Stopwatch.StartNew();

        await sut.GeocodeAsync("London");
        await sut.GeocodeAsync("Paris");

        sw.Stop();

        // Second request should have been delayed by ~500ms
        sw.ElapsedMilliseconds.Should().BeGreaterThan(400);
        await inner.Received(2).GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RateLimit_Configurable()
    {
        var inner = Substitute.For<IGeocodingService>();
        inner.GeocodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(SampleResult);

        // 1 req/sec — default Nominatim limit
        using var sut = CreateSut(inner, rateLimitPerSecond: 1);

        var sw = Stopwatch.StartNew();

        await sut.GeocodeAsync("London");
        await sut.GeocodeAsync("Paris");

        sw.Stop();

        sw.ElapsedMilliseconds.Should().BeGreaterThan(800);
    }

    [Fact]
    public async Task RateLimit_Dispose_DoesNotThrow()
    {
        var inner = Substitute.For<IGeocodingService>();
        var sut = CreateSut(inner);

        var act = () =>
        {
            sut.Dispose();
        };

        act.Should().NotThrow();
    }
}
