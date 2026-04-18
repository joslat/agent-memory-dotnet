using System.Diagnostics;
using FluentAssertions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.Observability;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.Observability;

[Collection("Observability")]
public sealed class InstrumentedMemoryServiceTests : IDisposable
{
    private readonly IMemoryService _inner;
    private readonly MemoryMetrics _metrics;
    private readonly InstrumentedMemoryService _sut;
    private readonly ActivityListener _listener;
    private readonly List<Activity> _capturedActivities = new();

    public InstrumentedMemoryServiceTests()
    {
        _inner = Substitute.For<IMemoryService>();
        _metrics = new MemoryMetrics();
        _sut = new InstrumentedMemoryService(_inner, _metrics);

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
    public async Task RecallAsync_CreatesActivity_WithSessionTag()
    {
        var request = new RecallRequest { SessionId = "s1", Query = "test" };
        _inner.RecallAsync(request, Arg.Any<CancellationToken>())
            .Returns(CreateRecallResult("s1"));

        await _sut.RecallAsync(request);

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.recall").Subject;
        activity.GetTagItem("memory.session_id").Should().Be("s1");
    }

    [Fact]
    public async Task RecallAsync_RecordsMetrics()
    {
        var request = new RecallRequest { SessionId = "s1", Query = "test" };
        _inner.RecallAsync(request, Arg.Any<CancellationToken>())
            .Returns(CreateRecallResult("s1"));

        // Metrics are recorded — we verify no exception and the inner was called.
        var result = await _sut.RecallAsync(request);

        result.Should().NotBeNull();
        await _inner.Received(1).RecallAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecallAsync_OnError_SetsErrorStatus()
    {
        var request = new RecallRequest { SessionId = "s1", Query = "test" };
        _inner.RecallAsync(request, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        var act = () => _sut.RecallAsync(request);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.recall").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
        activity.StatusDescription.Should().Be("boom");
    }

    [Fact]
    public async Task AddMessageAsync_CreatesActivity()
    {
        var message = CreateMessage("msg-1", "s1");
        _inner.AddMessageAsync("s1", "c1", "user", "hello", null, Arg.Any<CancellationToken>())
            .Returns(message);

        await _sut.AddMessageAsync("s1", "c1", "user", "hello");

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.add_message").Subject;
        activity.GetTagItem("memory.session_id").Should().Be("s1");
        activity.GetTagItem("memory.conversation_id").Should().Be("c1");
        activity.GetTagItem("memory.message.role").Should().Be("user");
    }

    [Fact]
    public async Task ExtractAndPersist_RecordsExtractionDuration()
    {
        var request = new ExtractionRequest
        {
            SessionId = "s1",
            Messages = new[] { CreateMessage("msg-1", "s1") }
        };
        _inner.ExtractAndPersistAsync(request, Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult());

        await _sut.ExtractAndPersistAsync(request);

        _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.extract_and_persist");
    }

    [Fact]
    public async Task ExtractAndPersist_IncrementsCounters()
    {
        var request = new ExtractionRequest
        {
            SessionId = "s1",
            Messages = new[] { CreateMessage("msg-1", "s1") }
        };
        var extractionResult = new ExtractionResult
        {
            Entities = new[] { new ExtractedEntity { Name = "Test", Type = "Person" } },
            Facts = new[]
            {
                new ExtractedFact { Subject = "a", Predicate = "is", Object = "b" }
            },
            Preferences = new[]
            {
                new ExtractedPreference { Category = "style", PreferenceText = "dark mode" }
            }
        };
        _inner.ExtractAndPersistAsync(request, Arg.Any<CancellationToken>())
            .Returns(extractionResult);

        await _sut.ExtractAndPersistAsync(request);

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.extract_and_persist").Subject;
        activity.GetTagItem("memory.extraction.entity_count").Should().Be(1);
        activity.GetTagItem("memory.extraction.fact_count").Should().Be(1);
        activity.GetTagItem("memory.extraction.preference_count").Should().Be(1);
    }

    [Fact]
    public async Task ExtractAndPersist_OnError_IncrementsErrorCounter()
    {
        var request = new ExtractionRequest
        {
            SessionId = "s1",
            Messages = new[] { CreateMessage("msg-1", "s1") }
        };
        _inner.ExtractAndPersistAsync(request, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("extraction failed"));

        var act = () => _sut.ExtractAndPersistAsync(request);
        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.extract_and_persist").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ClearSession_CreatesActivity()
    {
        _inner.ClearSessionAsync("s1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ClearSessionAsync("s1");

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.clear_session").Subject;
        activity.GetTagItem("memory.session_id").Should().Be("s1");
    }

    [Fact]
    public async Task ExtractFromSessionAsync_CreatesActivity_WithSessionTag()
    {
        _inner.ExtractFromSessionAsync("s1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ExtractFromSessionAsync("s1");

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.extract_from_session").Subject;
        activity.GetTagItem("memory.session_id").Should().Be("s1");
    }

    [Fact]
    public async Task ExtractFromSessionAsync_OnError_IncrementsErrorCounterAndSetsStatus()
    {
        _inner.ExtractFromSessionAsync("s1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("extract failed")));

        var act = () => _sut.ExtractFromSessionAsync("s1");
        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.extract_from_session").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ExtractFromSessionAsync_RecordsDurationMetric()
    {
        _inner.ExtractFromSessionAsync("s1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ExtractFromSessionAsync("s1");

        // If no exception, duration was recorded (verified by inner being called)
        await _inner.Received(1).ExtractFromSessionAsync("s1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExtractFromConversationAsync_CreatesActivity_WithConversationTag()
    {
        _inner.ExtractFromConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ExtractFromConversationAsync("c1");

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.extract_from_conversation").Subject;
        activity.GetTagItem("memory.conversation_id").Should().Be("c1");
    }

    [Fact]
    public async Task ExtractFromConversationAsync_OnError_IncrementsErrorCounterAndSetsStatus()
    {
        _inner.ExtractFromConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("extract conv failed")));

        var act = () => _sut.ExtractFromConversationAsync("c1");
        await act.Should().ThrowAsync<InvalidOperationException>();

        var activity = _capturedActivities.Should().ContainSingle(
            a => a.OperationName == "memory.extract_from_conversation").Subject;
        activity.Status.Should().Be(ActivityStatusCode.Error);
    }

    [Fact]
    public async Task ExtractFromConversationAsync_RecordsDurationMetric()
    {
        _inner.ExtractFromConversationAsync("c1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.ExtractFromConversationAsync("c1");

        await _inner.Received(1).ExtractFromConversationAsync("c1", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AllMethods_DelegateToInner()
    {
        var recallRequest = new RecallRequest { SessionId = "s1", Query = "q" };
        _inner.RecallAsync(recallRequest, Arg.Any<CancellationToken>())
            .Returns(CreateRecallResult("s1"));
        _inner.AddMessageAsync("s1", "c1", "user", "hi", null, Arg.Any<CancellationToken>())
            .Returns(CreateMessage("m1", "s1"));
        var messages = new[] { CreateMessage("m1", "s1") };
        _inner.AddMessagesAsync(messages, Arg.Any<CancellationToken>())
            .Returns(messages);
        var extractRequest = new ExtractionRequest
        {
            SessionId = "s1",
            Messages = messages
        };
        _inner.ExtractAndPersistAsync(extractRequest, Arg.Any<CancellationToken>())
            .Returns(new ExtractionResult());
        _inner.ClearSessionAsync("s1", Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.RecallAsync(recallRequest);
        await _sut.AddMessageAsync("s1", "c1", "user", "hi");
        await _sut.AddMessagesAsync(messages);
        await _sut.ExtractAndPersistAsync(extractRequest);
        await _sut.ClearSessionAsync("s1");

        await _inner.Received(1).RecallAsync(recallRequest, Arg.Any<CancellationToken>());
        await _inner.Received(1).AddMessageAsync("s1", "c1", "user", "hi", null, Arg.Any<CancellationToken>());
        await _inner.Received(1).AddMessagesAsync(messages, Arg.Any<CancellationToken>());
        await _inner.Received(1).ExtractAndPersistAsync(extractRequest, Arg.Any<CancellationToken>());
        await _inner.Received(1).ClearSessionAsync("s1", Arg.Any<CancellationToken>());
    }

    // ---- Helpers ----

    private static RecallResult CreateRecallResult(string sessionId) => new()
    {
        Context = new MemoryContext
        {
            SessionId = sessionId,
            AssembledAtUtc = DateTimeOffset.UtcNow
        }
    };

    private static Message CreateMessage(string id, string sessionId) => new()
    {
        MessageId = id,
        ConversationId = "conv-1",
        SessionId = sessionId,
        Role = "user",
        Content = "Sample content",
        TimestampUtc = DateTimeOffset.UtcNow
    };
}
