using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class ReasoningMemoryServiceTests
{
    private readonly IReasoningTraceRepository _traceRepo;
    private readonly IReasoningStepRepository _stepRepo;
    private readonly IToolCallRepository _toolCallRepo;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly DateTimeOffset _fixedTime = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public ReasoningMemoryServiceTests()
    {
        _traceRepo = Substitute.For<IReasoningTraceRepository>();
        _stepRepo = Substitute.For<IReasoningStepRepository>();
        _toolCallRepo = Substitute.For<IToolCallRepository>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _clock.UtcNow.Returns(_fixedTime);
        _idGenerator.GenerateId().Returns("generated-id-1", "generated-id-2", "generated-id-3");

        _traceRepo
            .AddAsync(Arg.Any<ReasoningTrace>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<ReasoningTrace>()));

        _traceRepo
            .UpdateAsync(Arg.Any<ReasoningTrace>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<ReasoningTrace>()));

        _stepRepo
            .AddAsync(Arg.Any<ReasoningStep>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<ReasoningStep>()));

        _toolCallRepo
            .AddAsync(Arg.Any<ToolCall>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<ToolCall>()));
    }

    private ReasoningMemoryService CreateSut() =>
        new(_traceRepo, _stepRepo, _toolCallRepo, _clock, _idGenerator,
            NullLogger<ReasoningMemoryService>.Instance);

    [Fact]
    public async Task StartTraceAsync_CreatesTraceWithGeneratedId()
    {
        _idGenerator.GenerateId().Returns("trace-id-1");
        var sut = CreateSut();

        var result = await sut.StartTraceAsync("session-1", "Solve the problem");

        result.TraceId.Should().Be("trace-id-1");
        result.SessionId.Should().Be("session-1");
        result.Task.Should().Be("Solve the problem");
    }

    [Fact]
    public async Task StartTraceAsync_SetsStartedAtFromClock()
    {
        var sut = CreateSut();

        var result = await sut.StartTraceAsync("session-1", "Test task");

        result.StartedAtUtc.Should().Be(_fixedTime);
    }

    [Fact]
    public async Task AddStepAsync_CreatesStepWithGeneratedId()
    {
        _idGenerator.GenerateId().Returns("step-id-1");
        var sut = CreateSut();

        var result = await sut.AddStepAsync("trace-1", 1, thought: "Thinking...");

        result.StepId.Should().Be("step-id-1");
        result.StepNumber.Should().Be(1);
        result.Thought.Should().Be("Thinking...");
    }

    [Fact]
    public async Task AddStepAsync_LinksToTrace()
    {
        var sut = CreateSut();

        var result = await sut.AddStepAsync("trace-abc", 2, action: "Do something");

        result.TraceId.Should().Be("trace-abc");
        await _stepRepo
            .Received(1)
            .AddAsync(Arg.Is<ReasoningStep>(s => s.TraceId == "trace-abc"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordToolCallAsync_CreatesToolCallWithAllProperties()
    {
        _idGenerator.GenerateId().Returns("tc-id-1");
        var sut = CreateSut();

        var result = await sut.RecordToolCallAsync(
            stepId: "step-1",
            toolName: "search_web",
            argumentsJson: """{"query":"test"}""",
            resultJson: """{"result":"found"}""",
            status: ToolCallStatus.Success,
            durationMs: 150L,
            error: null);

        result.ToolCallId.Should().Be("tc-id-1");
        result.StepId.Should().Be("step-1");
        result.ToolName.Should().Be("search_web");
        result.ArgumentsJson.Should().Be("""{"query":"test"}""");
        result.ResultJson.Should().Be("""{"result":"found"}""");
        result.Status.Should().Be(ToolCallStatus.Success);
        result.DurationMs.Should().Be(150L);
    }

    [Fact]
    public async Task CompleteTraceAsync_SetsOutcomeAndSuccess()
    {
        var existingTrace = CreateTrace("trace-1", "session-1");
        _traceRepo
            .GetByIdAsync("trace-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReasoningTrace?>(existingTrace));
        var sut = CreateSut();

        var result = await sut.CompleteTraceAsync("trace-1", outcome: "Solved successfully", success: true);

        result.Outcome.Should().Be("Solved successfully");
        result.Success.Should().BeTrue();
    }

    [Fact]
    public async Task CompleteTraceAsync_SetsCompletedAtFromClock()
    {
        var existingTrace = CreateTrace("trace-1", "session-1");
        _traceRepo
            .GetByIdAsync("trace-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReasoningTrace?>(existingTrace));
        var sut = CreateSut();

        var result = await sut.CompleteTraceAsync("trace-1");

        result.CompletedAtUtc.Should().Be(_fixedTime);
        await _traceRepo.Received(1).UpdateAsync(Arg.Any<ReasoningTrace>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTraceWithStepsAsync_ReturnsBothTraceAndSteps()
    {
        var trace = CreateTrace("trace-1", "session-1");
        var steps = new[] { CreateStep("step-1", "trace-1"), CreateStep("step-2", "trace-1") };
        _traceRepo
            .GetByIdAsync("trace-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReasoningTrace?>(trace));
        _stepRepo
            .GetByTraceAsync("trace-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningStep>>(steps));
        var sut = CreateSut();

        var (resultTrace, resultSteps) = await sut.GetTraceWithStepsAsync("trace-1");

        resultTrace.Should().Be(trace);
        resultSteps.Should().HaveCount(2);
    }

    [Fact]
    public async Task ListTracesAsync_DelegatesToRepository()
    {
        _traceRepo
            .ListBySessionAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(Array.Empty<ReasoningTrace>()));
        var sut = CreateSut();

        await sut.ListTracesAsync("session-1", 5);

        await _traceRepo
            .Received(1)
            .ListBySessionAsync("session-1", 5, Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SearchSimilarTracesAsync_StripsScores()
    {
        var trace = CreateTrace("trace-1", "session-1");
        _traceRepo
            .SearchByTaskVectorAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(ReasoningTrace, double)>>(new[] { (trace, 0.92) }));
        var sut = CreateSut();

        var result = await sut.SearchSimilarTracesAsync(new float[1536]);

        result.Should().ContainSingle();
        result[0].Should().Be(trace);
    }

    [Fact]
    public async Task SearchSimilarTracesAsOfAsync_PassesAsOf_AndStripsScores()
    {
        var asOf = new DateTimeOffset(2025, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var trace = CreateTrace("trace-1", "session-1");
        _traceRepo
            .SearchByTaskVectorAsOfAsync(
                Arg.Any<float[]>(), asOf, Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(ReasoningTrace, double)>>(new[] { (trace, 0.91) }));
        var sut = CreateSut();

        var result = await sut.SearchSimilarTracesAsOfAsync(new float[1536], asOf);

        result.Should().ContainSingle().Which.Should().Be(trace);
        await _traceRepo.Received(1).SearchByTaskVectorAsOfAsync(
            Arg.Any<float[]>(), asOf, Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordTouchedEntitiesAsync_DelegatesToRepository_AndReturnsCount()
    {
        var entityIds = new[] { "e1", "e2" };
        _stepRepo
            .LinkTouchedEntitiesAsync("step-1", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(2));
        var sut = CreateSut();

        var linked = await sut.RecordTouchedEntitiesAsync("step-1", entityIds);

        linked.Should().Be(2);
        await _stepRepo.Received(1).LinkTouchedEntitiesAsync(
            "step-1",
            Arg.Is<IReadOnlyList<string>>(ids => ids.SequenceEqual(entityIds)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordTouchedEntitiesAsync_EmptyList_ShortCircuitsWithoutHittingRepository()
    {
        var sut = CreateSut();

        var linked = await sut.RecordTouchedEntitiesAsync("step-1", Array.Empty<string>());

        linked.Should().Be(0);
        await _stepRepo.DidNotReceive().LinkTouchedEntitiesAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RecordTouchedEntitiesAsync_BlankStepId_Throws(string stepId)
    {
        var sut = CreateSut();

        await sut.Invoking(s => s.RecordTouchedEntitiesAsync(stepId, new[] { "e1" }))
            .Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task GetTouchedEntitiesAsync_DelegatesToRepository()
    {
        _stepRepo
            .GetTouchedEntityIdsAsync("step-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<string>>(new[] { "e1", "e2" }));
        var sut = CreateSut();

        var result = await sut.GetTouchedEntitiesAsync("step-1");

        result.Should().BeEquivalentTo("e1", "e2");
        await _stepRepo.Received(1).GetTouchedEntityIdsAsync("step-1", Arg.Any<CancellationToken>());
    }

    // ---- Helpers ----

    private static ReasoningTrace CreateTrace(string traceId, string sessionId) => new()
    {
        TraceId = traceId,
        SessionId = sessionId,
        Task = "Test task",
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    private static ReasoningStep CreateStep(string stepId, string traceId) => new()
    {
        StepId = stepId,
        TraceId = traceId,
        StepNumber = 1
    };
}
