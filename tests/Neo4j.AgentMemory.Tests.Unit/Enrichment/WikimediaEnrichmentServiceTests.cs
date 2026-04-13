using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Enrichment;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Enrichment;

public sealed class WikimediaEnrichmentServiceTests
{
    private const string ValidWikipediaResponse = """
        {
          "title": "London",
          "extract": "London is the capital and largest city of the United Kingdom.",
          "description": "Capital and largest city of the United Kingdom",
          "content_urls": {
            "desktop": {
              "page": "https://en.wikipedia.org/wiki/London"
            }
          },
          "thumbnail": {
            "source": "https://upload.wikimedia.org/wikipedia/commons/thumb/london.jpg"
          }
        }
        """;

    private static WikimediaEnrichmentService CreateSut(
        MockHttpMessageHandler handler,
        EnrichmentOptions? options = null)
    {
        var httpClient = new HttpClient(handler);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(httpClient);

        return new WikimediaEnrichmentService(
            factory,
            Options.Create(options ?? new EnrichmentOptions()),
            NullLogger<WikimediaEnrichmentService>.Instance);
    }

    [Fact]
    public async Task Enrich_ValidEntity_ReturnsResult()
    {
        var handler = new MockHttpMessageHandler(ValidWikipediaResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("London", "City");

        result.Should().NotBeNull();
        result!.EntityName.Should().Be("London");
        result.Summary.Should().Contain("capital");
        result.Provider.Should().Be("Wikipedia");
        result.RetrievedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Enrich_NotFound_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler("{}", HttpStatusCode.NotFound);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("NonExistentEntity_XYZ_123", "Unknown");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Enrich_HttpError_ReturnsNull_LogsWarning()
    {
        var handler = new MockHttpMessageHandler("{}", HttpStatusCode.InternalServerError);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("London", "City");

        result.Should().BeNull();
    }

    [Fact]
    public async Task Enrich_MapsAllFields()
    {
        var handler = new MockHttpMessageHandler(ValidWikipediaResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("London", "City");

        result.Should().NotBeNull();
        result!.Summary.Should().Be("London is the capital and largest city of the United Kingdom.");
        result.Description.Should().Be("Capital and largest city of the United Kingdom");
        result.WikipediaUrl.Should().Be("https://en.wikipedia.org/wiki/London");
        result.ImageUrl.Should().Contain("wikimedia.org");
    }

    [Fact]
    public async Task Enrich_SetsCorrectUrl()
    {
        var handler = new MockHttpMessageHandler(ValidWikipediaResponse);
        var options = new EnrichmentOptions { WikipediaLanguage = "en" };
        var sut = CreateSut(handler, options);

        await sut.EnrichEntityAsync("London", "City");

        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Contain("en.wikipedia.org");
        handler.LastRequest.RequestUri.ToString().Should().Contain("London");
        handler.LastRequest.RequestUri.ToString().Should().Contain("rest_v1/page/summary");
    }

    [Fact]
    public async Task Enrich_CancellationToken_Honored()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var handler = new MockHttpMessageHandler(ValidWikipediaResponse);
        var sut = CreateSut(handler);

        await sut.Invoking(s => s.EnrichEntityAsync("London", "City", cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Enrich_NullOrWhitespace_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler(ValidWikipediaResponse);
        var sut = CreateSut(handler);

        var result1 = await sut.EnrichEntityAsync(string.Empty, "City");
        var result2 = await sut.EnrichEntityAsync("   ", "City");

        result1.Should().BeNull();
        result2.Should().BeNull();
    }
}
