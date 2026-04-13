using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;
using Neo4j.AgentMemory.McpServer;
using Neo4j.AgentMemory.McpServer.Tools;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.McpServer;

public sealed class ReasoningToolsTests
{
    private readonly IReasoningMemoryService _reasoningMemory = Substitute.For<IReasoningMemoryService>();
    private readonly IOptions<McpServerOptions> _options = Options.Create(new McpServerOptions());

    private static readonly DateTimeOffset FixedTime = new(2025, 1, 15, 10, 0, 0, TimeSpan.Zero);

    // ── memory_start_trace ──

    [Fact]
    public async Task MemoryStartTrace_CallsStartTraceAsyncWithCorrectParameters()
    {
        var trace = CreateTrace();
        _reasoningMemory.StartTraceAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(trace);

        await ReasoningTools.MemoryStartTrace(_reasoningMemory, _options, "Solve problem", "ses-1");

        await _reasoningMemory.Received(1).StartTraceAsync(
            "ses-1", "Solve problem",
            Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryStartTrace_UsesDefaultSessionId()
    {
        _reasoningMemory.StartTraceAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateTrace());

        await ReasoningTools.MemoryStartTrace(_reasoningMemory, _options, "task");

        await _reasoningMemory.Received(1).StartTraceAsync(
            "default", "task",
            Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryStartTrace_ReturnsJsonWithTraceProperties()
    {
        _reasoningMemory.StartTraceAsync(
                Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateTrace());

        var result = await ReasoningTools.MemoryStartTrace(_reasoningMemory, _options, "Solve problem", "ses-1");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("traceId").GetString().Should().Be("trace-1");
        doc.RootElement.GetProperty("sessionId").GetString().Should().Be("ses-1");
        doc.RootElement.GetProperty("task").GetString().Should().Be("Solve problem");
    }

    // ── memory_record_step ──

    [Fact]
    public async Task MemoryRecordStep_CallsAddStepAsyncWithCorrectParameters()
    {
        var step = CreateStep();
        _reasoningMemory.AddStepAsync(
                Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(step);

        await ReasoningTools.MemoryRecordStep(
            _reasoningMemory, "trace-1", 1, "I think...", "do something", "it worked");

        await _reasoningMemory.Received(1).AddStepAsync(
            "trace-1", 1, "I think...", "do something", "it worked",
            Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryRecordStep_ReturnsJsonWithStepProperties()
    {
        _reasoningMemory.AddStepAsync(
                Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(CreateStep());

        var result = await ReasoningTools.MemoryRecordStep(
            _reasoningMemory, "trace-1", 1, "thinking", "acting", "observing");

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("stepId").GetString().Should().Be("step-1");
        doc.RootElement.GetProperty("traceId").GetString().Should().Be("trace-1");
        doc.RootElement.GetProperty("stepNumber").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("thought").GetString().Should().Be("thinking");
        doc.RootElement.GetProperty("action").GetString().Should().Be("acting");
        doc.RootElement.GetProperty("observation").GetString().Should().Be("observing");
    }

    [Fact]
    public async Task MemoryRecordStep_HandlesNullOptionalFields()
    {
        var step = new ReasoningStep
        {
            StepId = "step-2",
            TraceId = "trace-1",
            StepNumber = 2
        };
        _reasoningMemory.AddStepAsync(
                Arg.Any<string>(), Arg.Any<int>(),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<float[]?>(), Arg.Any<IReadOnlyDictionary<string, object>?>(),
                Arg.Any<CancellationToken>())
            .Returns(step);

        var result = await ReasoningTools.MemoryRecordStep(_reasoningMemory, "trace-1", 2);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("stepId").GetString().Should().Be("step-2");
        // Null fields should be omitted (WhenWritingNull)
        doc.RootElement.TryGetProperty("thought", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("action", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("observation", out _).Should().BeFalse();
    }

    // ── memory_complete_trace ──

    [Fact]
    public async Task MemoryCompleteTrace_CallsCompleteTraceAsyncWithCorrectParameters()
    {
        var trace = CreateTrace(outcome: "Done", success: true);
        _reasoningMemory.CompleteTraceAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool?>(),
                Arg.Any<CancellationToken>())
            .Returns(trace);

        await ReasoningTools.MemoryCompleteTrace(_reasoningMemory, "trace-1", "Done", true);

        await _reasoningMemory.Received(1).CompleteTraceAsync(
            "trace-1", "Done", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MemoryCompleteTrace_ReturnsJsonWithTraceProperties()
    {
        var completedTime = FixedTime.AddMinutes(5);
        var trace = new ReasoningTrace
        {
            TraceId = "trace-1",
            SessionId = "ses-1",
            Task = "Solve problem",
            Outcome = "Solved!",
            Success = true,
            StartedAtUtc = FixedTime,
            CompletedAtUtc = completedTime
        };
        _reasoningMemory.CompleteTraceAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool?>(),
                Arg.Any<CancellationToken>())
            .Returns(trace);

        var result = await ReasoningTools.MemoryCompleteTrace(_reasoningMemory, "trace-1", "Solved!", true);

        var doc = JsonDocument.Parse(result);
        doc.RootElement.GetProperty("traceId").GetString().Should().Be("trace-1");
        doc.RootElement.GetProperty("outcome").GetString().Should().Be("Solved!");
        doc.RootElement.GetProperty("success").GetBoolean().Should().BeTrue();
        doc.RootElement.TryGetProperty("completedAtUtc", out _).Should().BeTrue();
    }

    private static ReasoningTrace CreateTrace(string? outcome = null, bool? success = null) => new()
    {
        TraceId = "trace-1",
        SessionId = "ses-1",
        Task = "Solve problem",
        Outcome = outcome,
        Success = success,
        StartedAtUtc = FixedTime
    };

    private static ReasoningStep CreateStep() => new()
    {
        StepId = "step-1",
        TraceId = "trace-1",
        StepNumber = 1,
        Thought = "thinking",
        Action = "acting",
        Observation = "observing"
    };
}
