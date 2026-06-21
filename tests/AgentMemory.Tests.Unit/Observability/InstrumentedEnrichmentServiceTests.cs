using System.Diagnostics.Metrics;
using FluentAssertions;
using AgentMemory.Abstractions.Domain.Enrichment;
using AgentMemory.Abstractions.Services;
using AgentMemory.Observability;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Observability;

// Emits on the process-global MemoryActivitySource — must serialize with the other observability tests so
// a concurrently-active listener can't capture this class's activities (shared-state race).
[Collection("Observability")]
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

    // R6 cleanup: providers return a status-bearing result instead of throwing, so a non-null
    // Error/RateLimited result must count as an enrichment ERROR (not a success) in telemetry.

    private async Task<long> CaptureEnrichmentErrorsAsync(Func<Task> action)
    {
        long errors = 0;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == MemoryMetrics.MeterName) l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            if (instrument.Name == "memory.enrichment.errors") Interlocked.Add(ref errors, value);
        });
        listener.Start();
        await action();
        listener.Dispose();
        return Interlocked.Read(ref errors);
    }

    [Theory]
    [InlineData(EnrichmentStatus.Error)]
    [InlineData(EnrichmentStatus.RateLimited)]
    public async Task EnrichEntityAsync_StatusBearingFailure_CountsAsError(EnrichmentStatus status)
    {
        _inner.EnrichEntityAsync("Acme", "ORGANIZATION", Arg.Any<CancellationToken>())
            .Returns(new EnrichmentResult { EntityName = "Acme", Status = status });

        var errors = await CaptureEnrichmentErrorsAsync(() => _sut.EnrichEntityAsync("Acme", "ORGANIZATION"));

        errors.Should().Be(1, "a non-null Error/RateLimited result is a failure, not a successful enrichment");
    }

    [Theory]
    [InlineData(EnrichmentStatus.Success)]
    [InlineData(EnrichmentStatus.NotFound)]
    [InlineData(EnrichmentStatus.Skipped)]
    public async Task EnrichEntityAsync_NonFailureStatus_DoesNotCountAsError(EnrichmentStatus status)
    {
        _inner.EnrichEntityAsync("Acme", "ORGANIZATION", Arg.Any<CancellationToken>())
            .Returns(new EnrichmentResult { EntityName = "Acme", Status = status });

        var errors = await CaptureEnrichmentErrorsAsync(() => _sut.EnrichEntityAsync("Acme", "ORGANIZATION"));

        errors.Should().Be(0);
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
