using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Enrichment;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Enrichment;

public sealed class NominatimGeocodingServiceTests
{
    private const string ValidNominatimResponse = """
        [
          {
            "lat": "51.5074",
            "lon": "-0.1278",
            "display_name": "London, Greater London, England, United Kingdom",
            "address": {
              "city": "London",
              "state": "England",
              "country": "United Kingdom",
              "country_code": "gb"
            }
          }
        ]
        """;

    private static NominatimGeocodingService CreateSut(
        MockHttpMessageHandler handler,
        GeocodingOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        return new NominatimGeocodingService(
            factory,
            Options.Create(options ?? new GeocodingOptions()),
            NullLogger<NominatimGeocodingService>.Instance);
    }

    [Fact]
    public async Task Geocode_ValidLocation_ReturnsResult()
    {
        var handler = new MockHttpMessageHandler(ValidNominatimResponse);
        var sut = CreateSut(handler);

        var result = await sut.GeocodeAsync("London");

        result.Should().NotBeNull();
        result!.Latitude.Should().BeApproximately(51.5074, 0.0001);
        result.Longitude.Should().BeApproximately(-0.1278, 0.0001);
        result.FormattedAddress.Should().Contain("London");
        result.City.Should().Be("London");
        result.Region.Should().Be("England");
        result.Country.Should().Be("United Kingdom");
        result.Provider.Should().Be("Nominatim");
    }

    [Fact]
    public async Task Geocode_EmptyResponse_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler("[]");
        var sut = CreateSut(handler);

        var result = await sut.GeocodeAsync("NonExistentPlace_XYZ_123");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Geocode_HttpError_ReturnsNull_LogsWarning()
    {
        var handler = new MockHttpMessageHandler("{}", HttpStatusCode.ServiceUnavailable);
        var sut = CreateSut(handler);

        var result = await sut.GeocodeAsync("London");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Geocode_SetsCorrectUserAgent()
    {
        var handler = new MockHttpMessageHandler(ValidNominatimResponse);
        var options = new GeocodingOptions { UserAgent = "MyTestAgent/2.0" };
        var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(options.UserAgent);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        var sut = new NominatimGeocodingService(
            factory,
            Options.Create(options),
            NullLogger<NominatimGeocodingService>.Instance);

        await sut.GeocodeAsync("London");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.Headers.UserAgent.ToString().Should().Contain("MyTestAgent");
    }

    [Fact]
    public async Task Geocode_SetsCorrectUrl()
    {
        var handler = new MockHttpMessageHandler(ValidNominatimResponse);
        var options = new GeocodingOptions { BaseUrl = "https://nominatim.openstreetmap.org" };
        var sut = CreateSut(handler, options);

        await sut.GeocodeAsync("London UK");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Contain("/search");
        handler.LastRequest.RequestUri.ToString().Should().Contain("format=json");
        handler.LastRequest.RequestUri.ToString().Should().Contain("London");
    }

    [Fact]
    public async Task Geocode_CancellationToken_Honored()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new MockHttpMessageHandler(ValidNominatimResponse);
        var sut = CreateSut(handler);

        await sut.Invoking(s => s.GeocodeAsync("London", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Geocode_NullOrWhitespace_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler(ValidNominatimResponse);
        var sut = CreateSut(handler);

        var result1 = await sut.GeocodeAsync(string.Empty);
        var result2 = await sut.GeocodeAsync("   ");

        result1.Should().BeNull();
        result2.Should().BeNull();
    }
}
