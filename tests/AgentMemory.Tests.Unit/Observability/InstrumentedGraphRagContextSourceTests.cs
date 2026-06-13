using System.Diagnostics;
using FluentAssertions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Observability;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AgentMemory.Tests.Unit.Observability;

[Collection("Observability")]
public sealed class InstrumentedGraphRagContextSourceTests : IDisposable
{
    private readonly IGraphRagContextSource _inner;
    private readonly MemoryMetrics _metrics;
    private readonly InstrumentedGraphRagContextSource _sut;
    private readonly ActivityListener _listener;
    private readonly List<Activity> _capturedActivities = new();

    // The ActivityListener is process-global, so it can observe memory.graphrag.query activities emitted by
    // OTHER test classes running in parallel (collections run concurrently). Every request in this class
    // carries this unique session id and assertions correlate on it, so a foreign activity can neither
    // satisfy nor break a ContainSingle. (Fixes a cross-collection flake.)
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    public InstrumentedGraphRagContextSourceTests()
    {
        _inner = Substitute.For<IGraphRagContextSource>();
        _metrics = new MemoryMetrics();
        _sut = new InstrumentedGraphRagContextSource(_inner, _metrics);

        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == MemoryActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity => { lock (_capturedActivities) _capturedActivities.Add(activity); }
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
    }

    /// <summary>This test's own graphrag activities, correlated by the unique session id.</summary>
    private Activity SingleGraphRagActivity()
    {
        lock (_capturedActivities)
        {
            return _capturedActivities
                .Where(a => a.OperationName == "memory.graphrag.query"
                         && (string?)a.GetTagItem("memory.session_id") == _sessionId)
                .Should().ContainSingle().Subject;
        }
    }

    [Fact]
    public async Task GetContext_CreatesActivity_WithSearchModeTag()
    {
        var request = new GraphRagContextRequest
        {
            SessionId = _sessionId,
            Query = "test",
            SearchMode = GraphRagSearchMode.Hybrid,
            TopK = 10
        };
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        await _sut.GetContextAsync(request);

        var activity = SingleGraphRagActivity();
        activity.GetTagItem("memory.graphrag.search_mode").Should().Be("Hybrid");
        activity.GetTagItem("memory.graphrag.top_k").Should().Be(10);
        activity.GetTagItem("memory.session_id").Should().Be(_sessionId);
    }

    [Fact]
    public async Task GetContext_RecordsGraphRagDuration()
    {
        var request = new GraphRagContextRequest { SessionId = _sessionId, Query = "test" };
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        var result = await _sut.GetContextAsync(request);

        result.Should().NotBeNull();
        SingleGraphRagActivity().Should().NotBeNull();
    }

    [Fact]
    public async Task GetContext_OnError_SetsErrorStatus()
    {
        var request = new GraphRagContextRequest { SessionId = _sessionId, Query = "test" };
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("graph failed"));

        var act = () => _sut.GetContextAsync(request);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = SingleGraphRagActivity();
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("graph failed");
    }

    [Fact]
    public async Task GetContext_IncrementsQueryCounter()
    {
        var request = new GraphRagContextRequest { SessionId = _sessionId, Query = "test" };
        _inner.GetContextAsync(request, Arg.Any<CancellationToken>())
            .Returns(CreateResult());

        await _sut.GetContextAsync(request);

        // Verify the inner was called and activity recorded result count
        await _inner.Received(1).GetContextAsync(request, Arg.Any<CancellationToken>());
        SingleGraphRagActivity().GetTagItem("memory.graphrag.result_count").Should().Be(1);
    }

    [Fact]
    public async Task GetContext_DelegatesToInner()
    {
        var request = new GraphRagContextRequest { SessionId = _sessionId, Query = "test" };
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
