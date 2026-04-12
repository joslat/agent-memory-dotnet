using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.AgentFramework;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.AgentFramework;

public sealed class AgentTraceRecorderTests
{
    private readonly IReasoningMemoryService _reasoningService;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private static readonly DateTimeOffset FixedNow =
        new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public AgentTraceRecorderTests()
    {
        _reasoningService = Substitute.For<IReasoningMemoryService>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _clock.UtcNow.Returns(FixedNow);
        _idGenerator.GenerateId().Returns("gen-id");

        _reasoningService
            .StartTraceAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new ReasoningTrace
            {
                TraceId = "trace-1",
                SessionId = ci.ArgAt<string>(0),
                Task = ci.ArgAt<string>(1),
                StartedAtUtc = FixedNow
            }));

        _reasoningService
            .AddStepAsync(
                Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new ReasoningStep
            {
                StepId = "step-1",
                TraceId = ci.ArgAt<string>(0),
                StepNumber = ci.ArgAt<int>(1)
            }));

        _reasoningService
            .RecordToolCallAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<ToolCallStatus>(),
                Arg.Any<long?>(), Arg.Any<string?>(),
                Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new ToolCall
            {
                ToolCallId = "tc-1",
                StepId = ci.ArgAt<string>(0),
                ToolName = ci.ArgAt<string>(1),
                ArgumentsJson = ci.ArgAt<string>(2),
                Status = ci.ArgAt<ToolCallStatus>(4)
            }));

        _reasoningService
            .CompleteTraceAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool?>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new ReasoningTrace
            {
                TraceId = ci.ArgAt<string>(0),
                SessionId = "s-1",
                Task = "some task",
                StartedAtUtc = FixedNow,
                Outcome = ci.ArgAt<string?>(1)
            }));
    }

    private AgentTraceRecorder CreateSut() => new(
        _reasoningService, _clock, _idGenerator,
        NullLogger<AgentTraceRecorder>.Instance);

    [Fact]
    public async Task StartTrace_CreatesTraceWithCorrectFields()
    {
        var sut = CreateSut();

        var trace = await sut.StartTraceAsync("summarize document", "session-1");

        trace.Task.Should().Be("summarize document");
        trace.SessionId.Should().Be("session-1");
        await _reasoningService.Received(1).StartTraceAsync(
            "session-1", "summarize document",
            Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordStep_AddsStepToTrace()
    {
        var sut = CreateSut();
        await sut.StartTraceAsync("task", "session-1");

        var step = await sut.RecordStepAsync("trace-1", "thought", "I should search for Alice");

        step.TraceId.Should().Be("trace-1");
        await _reasoningService.Received(1).AddStepAsync(
            "trace-1", Arg.Any<int>(), "I should search for Alice", null, null,
            Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordToolCall_AddsToolCallToStep()
    {
        var sut = CreateSut();

        var toolCall = await sut.RecordToolCallAsync(
            "step-1", "search_memory", "{\"query\":\"Alice\"}", "{\"result\":\"found\"}");

        toolCall.ToolName.Should().Be("search_memory");
        await _reasoningService.Received(1).RecordToolCallAsync(
            "step-1", "search_memory", "{\"query\":\"Alice\"}", "{\"result\":\"found\"}",
            ToolCallStatus.Success,
            Arg.Any<long?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTrace_SetsOutcomeAndDuration()
    {
        var sut = CreateSut();
        await sut.StartTraceAsync("task", "session-1");

        await sut.CompleteTraceAsync("trace-1", "Task completed successfully");

        await _reasoningService.Received(1).CompleteTraceAsync(
            "trace-1", "Task completed successfully",
            Arg.Any<bool?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTrace_GeneratesUniqueIds()
    {
        var callCount = 0;
        _reasoningService
            .StartTraceAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                callCount++;
                return Task.FromResult(new ReasoningTrace
                {
                    TraceId = $"trace-{callCount}",
                    SessionId = "s-1",
                    Task = "task",
                    StartedAtUtc = FixedNow
                });
            });

        var sut = CreateSut();
        var t1 = await sut.StartTraceAsync("task one", "session-1");
        var t2 = await sut.StartTraceAsync("task two", "session-1");

        t1.TraceId.Should().NotBe(t2.TraceId);
    }

    [Fact]
    public async Task RecordStep_IncrementsStepNumber()
    {
        var sut = CreateSut();
        await sut.StartTraceAsync("task", "session-1");

        await sut.RecordStepAsync("trace-1", "thought", "step one");
        await sut.RecordStepAsync("trace-1", "action", "step two");

        await _reasoningService.Received(1).AddStepAsync(
            "trace-1", 1,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
        await _reasoningService.Received(1).AddStepAsync(
            "trace-1", 2,
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordToolCall_WithError_SetsErrorStatus()
    {
        var sut = CreateSut();

        await sut.RecordToolCallAsync("step-1", "search_memory", "{}", null, ToolCallStatus.Error);

        await _reasoningService.Received(1).RecordToolCallAsync(
            "step-1", "search_memory", "{}",
            null, ToolCallStatus.Error,
            Arg.Any<long?>(), Arg.Any<string?>(),
            Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteTrace_NonExistentTrace_LogsWarning()
    {
        var logger = new CapturingLogger<AgentTraceRecorder>();
        var sut = new AgentTraceRecorder(_reasoningService, _clock, _idGenerator, logger);

        // Don't call StartTrace — traceId is unknown to this recorder.
        await sut.CompleteTraceAsync("unknown-trace", "some outcome");

        logger.WarningCount.Should().Be(1);
    }

    /// <summary>Simple logger that counts warning-level log calls.</summary>
    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public int WarningCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                WarningCount++;
        }
    }
}
