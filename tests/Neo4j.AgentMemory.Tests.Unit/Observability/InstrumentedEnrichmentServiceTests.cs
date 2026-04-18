using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain.Enrichment;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Observability;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Observability;

public sealed class InstrumentedEnrichmentServiceTests
{
    private readonly IEnrichmentService _inner = Substitute.For<IEnrichmentService>();
    private readonly MemoryMetrics _metrics = new();
    private readonly InstrumentedEnrichmentService _sut;

    public InstrumentedEnrichmentServiceTests()
    {
        _sut = new InstrumentedEnrichmentService(_inner, _metrics);
    }

    [Fact]
    public async Task EnrichEntityAsync_DelegatesToInner()
    {
        var expected = new EnrichmentResult
        {
            EntityName = "Neo4j",
            Summary = "Graph database company"
        };
        _inner.EnrichEntityAsync("Neo4j", "ORGANIZATION", Arg.Any<CancellationToken>()).Returns(expected);

        var result = await _sut.EnrichEntityAsync("Neo4j", "ORGANIZATION");

        result.Should().BeSameAs(expected);
        await _inner.Received(1).EnrichEntityAsync("Neo4j", "ORGANIZATION", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnrichEntityAsync_ReturnsNull_WhenInnerReturnsNull()
    {
        _inner.EnrichEntityAsync("Unknown", "PERSON", Arg.Any<CancellationToken>())
            .Returns((EnrichmentResult?)null);

        var result = await _sut.EnrichEntityAsync("Unknown", "PERSON");

        result.Should().BeNull();
    }

    [Fact]
    public async Task EnrichEntityAsync_OnException_Rethrows()
    {
        _inner.EnrichEntityAsync("Test", "PERSON", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => _sut.EnrichEntityAsync("Test", "PERSON");
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
    }

    [Fact]
    public void Constructor_NullInner_Throws()
    {
        var act = () => new InstrumentedEnrichmentService(null!, _metrics);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullMetrics_Throws()
    {
        var act = () => new InstrumentedEnrichmentService(_inner, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
