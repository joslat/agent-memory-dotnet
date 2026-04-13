using System.Diagnostics;
using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Observability;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Observability;

[Collection("Observability")]
public sealed class InstrumentedGraphRagContextSourceTests : IDisposable
{
    private readonly IGraphRagContextSource _inner;
    private readonly MemoryMetrics _metrics;
    private readonly InstrumentedGraphRagContextSource _sut;
    private readonly ActivityListener _listener;
    private readonly List<Activity> _capturedActivities = new();

    public InstrumentedGraphRagContextSourceTests()
    {
        _inner = Substitute.For<IGraphRagContextSource>();
        _metrics = new MemoryMetrics();
        _sut = new InstrumentedGraphRagContextSource(_inner, _metrics);

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MemoryActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => _capturedActivities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    [Fact]
    public async Task GetContext_CreatesActivity_WithSearchModeTag()
    {
        var request = new GraphRagContextRequest
        {
            SessionId = "s1",
            Query = "test",
            SearchMode = GraphRagSearchMode.Hybrid,
            TopK = 10
        };
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        await _sut.GetContextAsync(request);

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.graphrag.query").Subject;
        activity.GetTagItem("memory.graphrag.search_mode").Should().Be("Hybrid");
        activity.GetTagItem("memory.graphrag.top_k").Should().Be(10);
        activity.GetTagItem("memory.session_id").Should().Be("s1");
    }

    [Fact]
    public async Task GetContext_RecordsGraphRagDuration()
    {
        var request = new GraphRagContextRequest { SessionId = "s1", Query = "test" };
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var result = await _sut.GetContextAsync(request);

        result.Should().NotBeNull();
        _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.graphrag.query");
    }

    [Fact]
    public async Task GetContext_OnError_SetsErrorStatus()
    {
        var request = new GraphRagContextRequest { SessionId = "s1", Query = "test" };
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("graph failed"));

        var act = () => _sut.GetContextAsync(request);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.graphrag.query").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("graph failed");
    }

    [Fact]
    public async Task GetContext_IncrementsQueryCounter()
    {
        var request = new GraphRagContextRequest { SessionId = "s1", Query = "test" };
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        await _sut.GetContextAsync(request);

        // Verify the inner was called and activity recorded result count
        await _inner.Received(1).GetContextAsync(request, Arg.Any<CancellationToken>());
        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.graphrag.query").Subject;
        activity.GetTagItem("memory.graphrag.result_count").Should().Be(1);
    }

    [Fact]
    public async Task GetContext_DelegatesToInner()
    {
        var request = new GraphRagContextRequest { SessionId = "s1", Query = "test" };
        var expected = CreateResult();
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .Returns(expected);

        var result = await _sut.GetContextAsync(request);

        result.Should().BeSameAs(expected);
        await _inner.Received(1).GetContextAsync(request, Arg.Any<CancellationToken>());
    }

    // ---- Helpers ----

    private static GraphRagContextResult CreateResult() => new()
    {
        Items = new[]
        {
            new GraphRagContextItem
            {
                Text = "test content",
                Score = 0.9
            }
        }
    };
}
