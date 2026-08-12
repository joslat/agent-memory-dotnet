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
    private readonly IEmbeddingOrchestrator _embeddingOrchestrator;
    private readonly IClock _clock;
    private readonly IIdGenerator _idGenerator;
    private readonly DateTimeOffset _fixedTime = new(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);

    public ReasoningMemoryServiceTests()
    {
        _traceRepo = Substitute.For<IReasoningTraceRepository>();
        _stepRepo = Substitute.For<IReasoningStepRepository>();
        _toolCallRepo = Substitute.For<IToolCallRepository>();
        _embeddingOrchestrator = Substitute.For<IEmbeddingOrchestrator>();
        _clock = Substitute.For<IClock>();
        _idGenerator = Substitute.For<IIdGenerator>();

        _clock.UtcNow.Returns(_fixedTime);
        _idGenerator.GenerateId().Returns("generated-id-1", "generated-id-2", "generated-id-3");
        _embeddingOrchestrator.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));

        _traceRepo
            .AddAsync(Arg.Any<ReasoningTrace>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<ReasoningTrace>()));

        _traceRepo
            .UpdateAsync(Arg.Any<ReasoningTrace>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<ReasoningTrace?>(ci.Arg<ReasoningTrace>()));

        _stepRepo
            .AddAsync(Arg.Any<ReasoningStep>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<ReasoningStep>()));

        _toolCallRepo
            .AddAsync(Arg.Any<ToolCall>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<ToolCall>()));
    }

    private ReasoningMemoryService CreateSut(ReasoningMemoryOptions? options = null) =>
        new(_traceRepo, _stepRepo, _toolCallRepo, _embeddingOrchestrator, _clock, _idGenerator,
            Microsoft.Extensions.Options.Options.Create(options ?? new ReasoningMemoryOptions()),
            NullLogger<ReasoningMemoryService>.Instance,
            new DefaultMemoryIsolationPolicy(Microsoft.Extensions.Options.Options.Create(new MemoryIsolationOptions()), NullLogger<DefaultMemoryIsolationPolicy>.Instance));

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

    // ── #92 Phase 3: a caller must never self-assign a bypassing trust level ────

    [Fact]
    public async Task StartTraceAsync_CallerSuppliedTrustLevel_IsStripped()
    {
        var sut = CreateSut();
        var callerMetadata = new Dictionary<string, object> { ["trust_level"] = "ApplicationTrusted" };

        var result = await sut.StartTraceAsync("session-1", "Test task", metadata: callerMetadata);

        // Asserts the caller's value was discarded, NOT that the result is Untrusted. Those were the
        // same thing until 6.3, when traces began carrying a configured trust level -- so the old
        // assertion was checking a proxy that has since stopped tracking the property. What must hold
        // is that ApplicationTrusted, which the caller asked for and which would bypass the admission
        // policy, is not what got stored.
        result.Metadata.GetTrustLevel().Should().NotBe(MemoryTrustLevel.ApplicationTrusted);
        result.Metadata.GetTrustLevel().Should()
            .Be(new ReasoningMemoryOptions().DefaultTraceTrustLevel);
    }

    [Fact]
    public async Task StartTraceAsync_CallerSuppliedTrustLevel_OtherMetadataStillPreserved()
    {
        var sut = CreateSut();
        var callerMetadata = new Dictionary<string, object> { ["trust_level"] = "ApplicationTrusted", ["source"] = "widget" };

        var result = await sut.StartTraceAsync("session-1", "Test task", metadata: callerMetadata);

        result.Metadata.Should().ContainKey("source");
    }

    [Fact]
    public async Task StartTraceAsync_NoMaxTracesConfigured_DoesNotPrune()
    {
        var sut = CreateSut(); // MaxTracesPerSession = null (default)

        await sut.StartTraceAsync("session-1", "Test task");

        await _traceRepo.DidNotReceive().PruneSessionTracesAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTraceAsync_MaxTracesConfigured_NullOwner_PrunesSharedBucket()
    {
        var sut = CreateSut(new ReasoningMemoryOptions { MaxTracesPerSession = 5 });

        await sut.StartTraceAsync("session-1", "Test task");

        // null ownerId now means "the shared bucket only", never "all owners".
        await _traceRepo.Received(1).PruneSessionTracesAsync(
            "session-1", 5, (string?)null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTraceAsync_MaxTracesConfiguredWithOwner_PrunesOwnBucketOnly()
    {
        var sut = CreateSut(new ReasoningMemoryOptions { MaxTracesPerSession = 3 });

        await sut.StartTraceAsync("session-1", "Test task", ownerId: "alice");

        await _traceRepo.Received(1).PruneSessionTracesAsync(
            "session-1", 3, "alice", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTraceAsync_MaxTracesZeroOrNegative_DoesNotPrune()
    {
        var sut = CreateSut(new ReasoningMemoryOptions { MaxTracesPerSession = 0 });

        await sut.StartTraceAsync("session-1", "Test task");

        await _traceRepo.DidNotReceive().PruneSessionTracesAsync(
            Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTraceAsync_PruneFailure_DoesNotFailStartTrace()
    {
        _traceRepo.PruneSessionTracesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<Task<int>>(_ => throw new InvalidOperationException("boom"));
        var sut = CreateSut(new ReasoningMemoryOptions { MaxTracesPerSession = 2 });

        var result = await sut.StartTraceAsync("session-1", "Test task");

        result.SessionId.Should().Be("session-1"); // trace still returned despite prune failure
    }

    [Fact]
    public async Task StartTraceAsync_GenerateTaskEmbeddingsEnabled_AutoEmbedsTaskWhenNoneSupplied()
    {
        var sut = CreateSut(new ReasoningMemoryOptions { GenerateTaskEmbeddings = true });

        await sut.StartTraceAsync("session-1", "Summarize the report");

        await _embeddingOrchestrator.Received(1).EmbedAsync("Summarize the report", Arg.Any<CancellationToken>());
        await _traceRepo.Received(1).AddAsync(
            Arg.Is<ReasoningTrace>(t => t.TaskEmbedding != null && t.TaskEmbedding.Length == 8), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTraceAsync_GenerateTaskEmbeddingsDisabled_DoesNotEmbed()
    {
        var sut = CreateSut(new ReasoningMemoryOptions { GenerateTaskEmbeddings = false });

        await sut.StartTraceAsync("session-1", "Summarize the report");

        await _embeddingOrchestrator.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _traceRepo.Received(1).AddAsync(
            Arg.Is<ReasoningTrace>(t => t.TaskEmbedding == null), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartTraceAsync_CallerSuppliedEmbedding_IsNotOverwritten()
    {
        var supplied = new float[] { 1f, 2f, 3f };
        var sut = CreateSut(new ReasoningMemoryOptions { GenerateTaskEmbeddings = true });

        await sut.StartTraceAsync("session-1", "task", taskEmbedding: supplied);

        await _embeddingOrchestrator.DidNotReceive().EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _traceRepo.Received(1).AddAsync(
            Arg.Is<ReasoningTrace>(t => t.TaskEmbedding == supplied), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordToolCallAsync_StoreToolCallsFalse_DoesNotPersist_ButReturnsRecord()
    {
        var sut = CreateSut(new ReasoningMemoryOptions { StoreToolCalls = false });

        var result = await sut.RecordToolCallAsync("step-1", "search", "{}");

        result.ToolName.Should().Be("search");
        await _toolCallRepo.DidNotReceive().AddAsync(Arg.Any<ToolCall>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RecordToolCallAsync_StoreToolCallsTrue_Persists()
    {
        var sut = CreateSut(new ReasoningMemoryOptions { StoreToolCalls = true });

        await sut.RecordToolCallAsync("step-1", "search", "{}");

        await _toolCallRepo.Received(1).AddAsync(Arg.Any<ToolCall>(), Arg.Any<CancellationToken>());
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
    public async Task CompleteTraceAsync_TraceConcurrentlyDeleted_ThrowsTypedTraceNotFound()
    {
        // R6-E: the trace exists at GetById but is concurrently deleted (session clear / retention prune)
        // before UpdateAsync, which now returns null. The service must surface the SAME typed TraceNotFound
        // as the read-miss path — not let an opaque sequence-empty exception escape.
        var existingTrace = CreateTrace("trace-1", "session-1");
        _traceRepo
            .GetByIdAsync("trace-1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReasoningTrace?>(existingTrace));
        _traceRepo
            .UpdateAsync(Arg.Any<ReasoningTrace>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<ReasoningTrace?>(null));
        var sut = CreateSut();

        var act = async () => await sut.CompleteTraceAsync("trace-1", outcome: "done", success: true);

        (await act.Should().ThrowAsync<AgentMemory.Abstractions.Exceptions.MemoryException>())
            .Which.Code.Should().Be(AgentMemory.Abstractions.Exceptions.MemoryErrorCodes.TraceNotFound);
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
