using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Neo4j.Repositories;
using AgentMemory.Tests.Integration.Fixtures;

namespace AgentMemory.Tests.Integration.Repositories;

[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class ToolCallStatsIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly Neo4jReasoningTraceRepository _traceRepo;
    private readonly Neo4jReasoningStepRepository _stepRepo;
    private readonly Neo4jToolCallRepository _toolRepo;

    public ToolCallStatsIntegrationTests(Neo4jIntegrationFixture fixture)
    {
        _fixture = fixture;
        _traceRepo = new Neo4jReasoningTraceRepository(fixture.TransactionRunner, NullLogger<Neo4jReasoningTraceRepository>.Instance);
        _stepRepo = new Neo4jReasoningStepRepository(fixture.TransactionRunner, NullLogger<Neo4jReasoningStepRepository>.Instance);
        _toolRepo = new Neo4jToolCallRepository(fixture.TransactionRunner, NullLogger<Neo4jToolCallRepository>.Instance);
    }

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    // Seeds a trace (owned as given) + one step, returns the step id so tool calls can hang off it.
    private async Task<string> SeedStepAsync(string? owner)
    {
        var traceId = $"trace-{Guid.NewGuid():N}";
        await _traceRepo.AddAsync(new ReasoningTrace
        {
            TraceId = traceId,
            SessionId = "s",
            Task = "t",
            OwnerId = owner,
            StartedAtUtc = new DateTimeOffset(2025, 6, 1, 0, 0, 0, TimeSpan.Zero),
        });
        var stepId = $"step-{Guid.NewGuid():N}";
        await _stepRepo.AddAsync(new ReasoningStep { StepId = stepId, TraceId = traceId, StepNumber = 1 });
        return stepId;
    }

    private Task AddCallAsync(string stepId, string tool, ToolCallStatus status, long? durationMs) =>
        _toolRepo.AddAsync(new ToolCall
        {
            ToolCallId = $"tc-{Guid.NewGuid():N}",
            StepId = stepId,
            ToolName = tool,
            ArgumentsJson = "{}",
            Status = status,
            DurationMs = durationMs,
        });

    [Fact]
    public async Task GetStatsAsync_AggregatesByTool_WithSuccessRateAndAvgDuration()
    {
        var step = await SeedStepAsync(owner: null);
        await AddCallAsync(step, "search", ToolCallStatus.Success, 100);
        await AddCallAsync(step, "search", ToolCallStatus.Success, 300);
        await AddCallAsync(step, "search", ToolCallStatus.Error, 200);
        await AddCallAsync(step, "fetch", ToolCallStatus.Timeout, 50);

        var stats = await _toolRepo.GetStatsAsync();

        stats.Should().HaveCount(2);
        var search = stats.Single(s => s.ToolName == "search");
        search.TotalCalls.Should().Be(3);
        search.SuccessfulCalls.Should().Be(2);
        search.FailedCalls.Should().Be(1);
        search.SuccessRate.Should().BeApproximately(2.0 / 3.0, 1e-9);
        search.AvgDurationMs.Should().BeApproximately(200.0, 1e-9); // (100+300+200)/3

        var fetch = stats.Single(s => s.ToolName == "fetch");
        fetch.TotalCalls.Should().Be(1);
        fetch.FailedCalls.Should().Be(1, "timeout classifies as failed");
        fetch.SuccessRate.Should().Be(0.0);
    }

    [Fact]
    public async Task GetStatsAsync_FilterByToolName_ReturnsOnlyThatTool()
    {
        var step = await SeedStepAsync(owner: null);
        await AddCallAsync(step, "search", ToolCallStatus.Success, 10);
        await AddCallAsync(step, "fetch", ToolCallStatus.Success, 10);

        var stats = await _toolRepo.GetStatsAsync("search");

        stats.Should().ContainSingle().Which.ToolName.Should().Be("search");
    }

    [Fact]
    public async Task GetStatsAsync_EmptyStore_ReturnsEmpty()
    {
        var stats = await _toolRepo.GetStatsAsync();
        stats.Should().BeEmpty();
    }

    [Fact]
    public async Task GetStatsAsync_OwnerScoped_ExcludesOtherOwnersToolCalls()
    {
        var aliceStep = await SeedStepAsync(owner: "alice");
        var bobStep = await SeedStepAsync(owner: "bob");
        await AddCallAsync(aliceStep, "search", ToolCallStatus.Success, 10);
        await AddCallAsync(bobStep, "search", ToolCallStatus.Success, 10);

        var stats = await _toolRepo.GetStatsAsync("search", MemoryScope.For("alice"));

        stats.Should().ContainSingle();
        stats[0].TotalCalls.Should().Be(1, "bob's tool call is out of alice's scope (owner lives on the trace)");
    }
}
