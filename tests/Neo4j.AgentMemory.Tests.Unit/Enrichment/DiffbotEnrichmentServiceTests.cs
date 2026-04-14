using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Options;
using Neo4j.AgentMemory.Enrichment;

namespace Neo4j.AgentMemory.Tests.Unit.Enrichment;

public sealed class DiffbotEnrichmentServiceTests
{
    // ---- Fixtures ----

    private static DiffbotEnrichmentService CreateSut(
        HttpMessageHandler handler,
        DiffbotEnrichmentOptions? options = null)
    {
        var opts = options ?? new DiffbotEnrichmentOptions { ApiKey = "test-key" };
        var client = new HttpClient(handler) { Timeout = opts.Timeout };
        return new DiffbotEnrichmentService(
            client,
            opts,
            NullLogger<DiffbotEnrichmentService>.Instance);
    }

    private const string PersonResponse = """
        {
          "data": [
            {
              "diffbotUri": "https://diffbot.com/entity/PERSON123",
              "name": "Alan Turing",
              "description": "British mathematician and computer scientist.",
              "summary": "Father of modern computing.",
              "image": "https://example.com/turing.jpg",
              "images": ["https://example.com/turing2.jpg"],
              "importance": 80,
              "nbIncomingEdges": 5000,
              "types": ["Person"],
              "birthDate": "1912-06-23",
              "deathDate": "1954-06-07",
              "gender": "Male",
              "nationalities": ["British"],
              "educations": [{"name": "King's College Cambridge"}],
              "employments": [{"name": "Bletchley Park", "diffbotUri": "https://diffbot.com/entity/BP"}],
              "employers": [{"name": "Bletchley Park", "diffbotUri": "https://diffbot.com/entity/BP"}],
              "homepageUri": "https://en.wikipedia.org/wiki/Alan_Turing"
            }
          ]
        }
        """;

    private const string OrgResponse = """
        {
          "data": [
            {
              "diffbotUri": "https://diffbot.com/entity/APPLE123",
              "name": "Apple Inc.",
              "description": "American multinational technology company.",
              "summary": "Maker of iPhone and Mac.",
              "image": "https://example.com/apple.jpg",
              "importance": 100,
              "types": ["Organization"],
              "foundingDate": {"str": "1976"},
              "nbEmployees": 164000,
              "revenue": {"value": 394000000000},
              "industries": ["Technology"],
              "categories": ["Consumer Electronics"],
              "isPublic": true,
              "stock": {"symbol": "AAPL", "exchange": "NASDAQ"},
              "subsidiaries": [{"name": "Beats Electronics", "diffbotUri": "https://diffbot.com/entity/BEATS"}],
              "homepageUri": "https://www.apple.com"
            }
          ]
        }
        """;

    private const string LocationResponse = """
        {
          "data": [
            {
              "diffbotUri": "https://diffbot.com/entity/LONDON123",
              "name": "London",
              "description": "Capital city of England and the United Kingdom.",
              "summary": "Major global city.",
              "importance": 95,
              "types": ["Place"],
              "country": {"name": "United Kingdom"},
              "region": {"name": "England"},
              "city": {"name": "London"},
              "latitude": 51.5074,
              "longitude": -0.1278,
              "population": 9000000
            }
          ]
        }
        """;

    private const string EmptyResponse = """{"data": []}""";

    // ---- Tests ----

    [Fact]
    public async Task EnrichEntity_PersonType_ReturnsExpectedResult()
    {
        var handler = new MockHttpMessageHandler(PersonResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Alan Turing", "PERSON");

        result.Should().NotBeNull();
        result!.EntityName.Should().Be("Alan Turing");
        result.EntityType.Should().Be("PERSON");
        result.Provider.Should().Be("diffbot");
        result.Status.Should().Be(EnrichmentStatus.Success);
        result.Description.Should().Contain("mathematician");
        result.Summary.Should().Contain("computing");
        result.DiffbotUri.Should().Contain("PERSON123");
        result.ImageUrl.Should().Contain("turing.jpg");
        result.SourceUrl.Should().Contain("wikipedia.org");
        result.Properties.Should().ContainKey("birthDate");
        result.Properties.Should().ContainKey("gender");
        result.RetrievedAtUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EnrichEntity_OrganizationType_ReturnsOrgMetadata()
    {
        var handler = new MockHttpMessageHandler(OrgResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Apple Inc.", "ORGANIZATION");

        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrichmentStatus.Success);
        result.EntityType.Should().Be("ORGANIZATION");
        result.Description.Should().Contain("technology");
        result.Properties.Should().ContainKey("nbEmployees");
        result.Properties.Should().ContainKey("isPublic");
        result.Properties.Should().ContainKey("industries");
        result.RelatedEntities.Should().Contain(r => r.Name == "Beats Electronics" && r.Relation == "subsidiaries");
    }

    [Fact]
    public async Task EnrichEntity_LocationType_ReturnsGeoData()
    {
        var handler = new MockHttpMessageHandler(LocationResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("London", "LOCATION");

        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrichmentStatus.Success);
        result.EntityType.Should().Be("LOCATION");
        result.Properties.Should().ContainKey("latitude");
        result.Properties.Should().ContainKey("longitude");
        result.Properties.Should().ContainKey("population");
        result.Properties.Should().ContainKey("country");
    }

    [Fact]
    public async Task EnrichEntity_UnsupportedType_ReturnsSkipped()
    {
        var handler = new MockHttpMessageHandler(EmptyResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("SomeEntity", "UNKNOWN_TYPE");

        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrichmentStatus.Skipped);
        result.ErrorMessage.Should().Contain("not supported");
        // Should not hit HTTP at all
        handler.LastRequest.Should().BeNull();
    }

    [Fact]
    public async Task EnrichEntity_NotFound_ReturnsNotFound()
    {
        var handler = new MockHttpMessageHandler(EmptyResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("NonExistentXYZ999", "PERSON");

        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrichmentStatus.NotFound);
        result.Description.Should().BeNull();
    }

    [Fact]
    public async Task EnrichEntity_InvalidApiKey_ReturnsError()
    {
        var handler = new MockHttpMessageHandler("{}", HttpStatusCode.Unauthorized);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Tesla", "ORGANIZATION");

        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrichmentStatus.Error);
        result.ErrorMessage.Should().Contain("API key");
    }

    [Fact]
    public async Task EnrichEntity_RateLimited_ReturnsRateLimited()
    {
        var handler = new MockHttpMessageHandler("{}", HttpStatusCode.TooManyRequests);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Tesla", "ORGANIZATION");

        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrichmentStatus.RateLimited);
        result.ErrorMessage.Should().Contain("Rate limited");
    }

    [Fact]
    public async Task EnrichEntity_HttpError_ReturnsError()
    {
        var handler = new MockHttpMessageHandler("{}", HttpStatusCode.InternalServerError);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Tesla", "ORGANIZATION");

        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrichmentStatus.Error);
        result.ErrorMessage.Should().Contain("500");
    }

    [Fact]
    public async Task EnrichEntity_Timeout_ReturnsError()
    {
        var handler = new ThrowingHttpMessageHandler(new TaskCanceledException("Request timed out"));
        var opts = new DiffbotEnrichmentOptions { ApiKey = "key", RateLimitSeconds = 0 };
        var sut = CreateSut(handler, opts);

        var result = await sut.EnrichEntityAsync("Tesla", "ORGANIZATION");

        result.Should().NotBeNull();
        result!.Status.Should().Be(EnrichmentStatus.Error);
        result.ErrorMessage.Should().Contain("timed out");
    }

    [Fact]
    public async Task EnrichEntity_CalculatesConfidence_FromImportanceScore()
    {
        // importance=80 → confidence = min(1.0, 80/100 + 0.5) = min(1.0, 1.3) = 1.0
        var handler = new MockHttpMessageHandler(PersonResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Alan Turing", "PERSON");

        result!.Confidence.Should().Be(1.0);
    }

    [Fact]
    public async Task EnrichEntity_CalculatesConfidence_LowImportance()
    {
        // importance=0 → confidence = min(1.0, 0/100 + 0.5) = 0.5
        const string lowImportanceResponse = """
            {
              "data": [
                {
                  "diffbotUri": "https://diffbot.com/entity/X1",
                  "name": "Unknown Person",
                  "importance": 0,
                  "types": ["Person"]
                }
              ]
            }
            """;

        var handler = new MockHttpMessageHandler(lowImportanceResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Unknown Person", "PERSON");

        result!.Confidence.Should().Be(0.5);
    }

    [Fact]
    public async Task EnrichEntity_ParsesRelatedEntities()
    {
        var handler = new MockHttpMessageHandler(PersonResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Alan Turing", "PERSON");

        result!.RelatedEntities.Should().NotBeEmpty();
        result.RelatedEntities.Should().Contain(r =>
            r.Name == "Bletchley Park" && r.Relation == "employers");
        result.RelatedEntities[0].DiffbotUri.Should().Contain("BP");
    }

    [Fact]
    public async Task EnrichEntity_ParsesImages()
    {
        var handler = new MockHttpMessageHandler(PersonResponse);
        var sut = CreateSut(handler);

        var result = await sut.EnrichEntityAsync("Alan Turing", "PERSON");

        result!.Images.Should().HaveCount(2);
        result.Images.Should().Contain("https://example.com/turing.jpg");
        result.Images.Should().Contain("https://example.com/turing2.jpg");
        result.ImageUrl.Should().Be("https://example.com/turing.jpg");
    }

    [Fact]
    public void DiffbotEnrichmentOptions_DefaultValues()
    {
        var opts = new DiffbotEnrichmentOptions();

        opts.ApiKey.Should().BeEmpty();
        opts.RateLimitSeconds.Should().Be(0.2);
        opts.Timeout.Should().Be(TimeSpan.FromSeconds(15));
        opts.BaseUrl.Should().Be("https://kg.diffbot.com/kg/v3");
    }

    [Fact]
    public async Task EnrichEntity_NullOrWhitespace_ReturnsNull()
    {
        var handler = new MockHttpMessageHandler(PersonResponse);
        var sut = CreateSut(handler);

        var result1 = await sut.EnrichEntityAsync(string.Empty, "PERSON");
        var result2 = await sut.EnrichEntityAsync("   ", "PERSON");

        result1.Should().BeNull();
        result2.Should().BeNull();
    }

    // ---- Helper handler that throws on send ----

    private sealed class ThrowingHttpMessageHandler(Exception exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(exception);
    }
}
