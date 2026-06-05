using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// Round-trip coverage for the ReasoningStep/ToolCall server-assigned <c>timestamp</c> (gaps G1/G2):
/// both are written via <c>datetime()</c> on create and must now be read back into
/// <see cref="ReasoningStep.TimestampUtc"/> / <see cref="ToolCall.TimestampUtc"/>.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ReasoningTimestampIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jReasoningTraceRepository _traces;
    private readonly Neo4jReasoningStepRepository _steps;
    private readonly Neo4jToolCallRepository _toolCalls;

    public ReasoningTimestampIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _traces = new Neo4jReasoningTraceRepository(fixture.TransactionRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance);
        _steps = new Neo4jReasoningStepRepository(fixture.TransactionRunner, NullLogger<Neo4jReasoningStepRepository>.Instance);
        _toolCalls = new Neo4jToolCallRepository(fixture.TransactionRunner, NullLogger<Neo4jToolCallRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ReasoningStep_TimestampUtc_IsWrittenAndReadBack()
    {
        var traceId = await SeedTraceAsync();
        var step = new ReasoningStep
        {
            StepId = $"step-{Guid.NewGuid():N}",
            TraceId = traceId,
            StepNumber = 1,
            Thought = "analyzing"
        };

        var added = await _steps.AddAsync(step);
        added.TimestampUtc.Should().NotBeNull("the create query assigns timestamp = datetime()");

        var fetched = await _steps.GetByIdAsync(step.StepId);
        fetched.Should().NotBeNull();
        fetched!.TimestampUtc.Should().NotBeNull();
        fetched.TimestampUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task ToolCall_TimestampUtc_IsWrittenAndReadBack()
    {
        var traceId = await SeedTraceAsync();
        var step = new ReasoningStep
        {
            StepId = $"step-{Guid.NewGuid():N}",
            TraceId = traceId,
            StepNumber = 1
        };
        await _steps.AddAsync(step);

        var toolCall = new ToolCall
        {
            ToolCallId = $"tc-{Guid.NewGuid():N}",
            StepId = step.StepId,
            ToolName = "search_tool",
            ArgumentsJson = "{}",
            Status = ToolCallStatus.Success
        };

        var added = await _toolCalls.AddAsync(toolCall);
        added.TimestampUtc.Should().NotBeNull("the create query assigns timestamp = datetime()");

        var fetched = await _toolCalls.GetByIdAsync(toolCall.ToolCallId);
        fetched.Should().NotBeNull();
        fetched!.TimestampUtc.Should().NotBeNull();
        fetched.TimestampUtc.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5));
    }

    private async Task<string> SeedTraceAsync()
    {
        var trace = new ReasoningTrace
        {
            TraceId = $"trace-{Guid.NewGuid():N}",
            SessionId = $"session-{Guid.NewGuid():N}",
            Task = "seed task",
            StartedAtUtc = DateTimeOffset.UtcNow
        };
        await _traces.AddAsync(trace);
        return trace.TraceId;
    }
}
