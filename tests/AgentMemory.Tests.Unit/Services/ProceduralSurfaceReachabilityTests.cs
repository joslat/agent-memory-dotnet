using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Procedural memory must be reachable from the public surface (15.2, 15.3, 15.5).
/// </summary>
/// <remarks>
/// <para>
/// The tier was complete in Cypher — <c>trace_kind</c>, its index, a procedures-only vector filter,
/// a prune exemption, eight live-database tests — and unreachable from outside. A consumer could not
/// promote a trace, could not ask for procedures, and on two of the four read surfaces could not see
/// one. "Shipped" and "usable" had come apart.
/// </para>
/// <para>
/// These tests hold the seams, because each gap was invisible from every layer except the one it
/// lived in: the repository took the argument, the service passed <c>null</c>, and nothing failed.
/// </para>
/// </remarks>
public sealed class ProceduralSurfaceReachabilityTests
{
    private readonly IReasoningTraceRepository _traces = Substitute.For<IReasoningTraceRepository>();

    private IReasoningMemoryService CreateSut()
    {
        var isolation = Substitute.For<IMemoryIsolationPolicy>();
        isolation.ResolveReadScope(
                Arg.Any<MemoryScope?>(), Arg.Any<string?>(), Arg.Any<string>(),
                Arg.Any<MemoryOperationAccess>())
            .Returns((MemoryScope?)null);

        return new ReasoningMemoryService(
            _traces,
            Substitute.For<IReasoningStepRepository>(),
            Substitute.For<IToolCallRepository>(),
            Substitute.For<IEmbeddingOrchestrator>(),
            Substitute.For<IClock>(),
            Substitute.For<IIdGenerator>(),
            Microsoft.Extensions.Options.Options.Create(new ReasoningMemoryOptions()),
            NullLogger<ReasoningMemoryService>.Instance,
            isolation);
    }

    [Fact]
    public async Task AConsumerCanPromoteATrace()
    {
        // 15.2. There was no way to do this at all: promotion existed only on a repository the
        // container does not hand out, so the marker every procedural query filters on could never be
        // written by a consumer.
        _traces.PromoteAsync("t1", TraceKind.Procedure, Arg.Any<CancellationToken>())
            .Returns(new ReasoningTrace
            {
                TraceId = "t1", SessionId = "s1", Task = "book a train",
                Kind = TraceKind.Procedure, StartedAtUtc = DateTimeOffset.UtcNow,
            });

        var promoted = await CreateSut().PromoteTraceAsync("t1", TraceKind.Procedure);

        promoted!.Kind.Should().Be(TraceKind.Procedure);
        await _traces.Received(1).PromoteAsync("t1", TraceKind.Procedure, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AProceduresOnlySearchActuallyFiltersForProcedures()
    {
        // 15.3. THE seam. The repository has taken proceduresOnly since 7.3; the service passed a
        // hardcoded null, so every shipped recall path asked for "any trace" and an agent looking for
        // how it did something before got episodes -- the wrong precedent library, returned
        // confidently. Asserted on the ARGUMENT because that is exactly what was being dropped.
        _traces.SearchByTaskVectorAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<int>(),
                Arg.Any<double>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await CreateSut().SearchSimilarTracesAsync(
            new float[8], proceduresOnly: true, successFilter: null);

        await _traces.Received(1).SearchByTaskVectorAsync(
            Arg.Any<float[]>(),
            Arg.Is<bool?>(value => value == true),
            Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheUnfilteredOverloadStillAsksForEverything()
    {
        // The off state must be unchanged, or every sealed measurement taken through the old overload
        // becomes incomparable with anything taken after.
        _traces.SearchByTaskVectorAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await CreateSut().SearchSimilarTracesAsync(new float[8]);

        await _traces.Received(1).SearchByTaskVectorAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheCoreFormatterRendersProceduralMemory()
    {
        // 15.5. Semantic Kernel and every direct-Core consumer were blind to the tier, while a trace
        // vector search ran on each recall and its results counted toward TotalItemsRetrieved.
        var result = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1",
                AssembledAtUtc = DateTimeOffset.UtcNow,
                SimilarTraces = new MemoryContextSection<ReasoningTrace>
                {
                    Items =
                    [
                        new ReasoningTrace
                        {
                            TraceId = "t1", SessionId = "s1",
                            Task = "book a train",
                            Outcome = "refresh the session first, then place the hold",
                            Success = true, Kind = TraceKind.Procedure,
                            StartedAtUtc = DateTimeOffset.UtcNow,
                        },
                    ],
                },
            },
            TotalItemsRetrieved = 1,
        };

        var formatted = MemoryContextFormatter.FormatRecallResult(result);

        formatted.Should().Contain("book a train");
        formatted.Should().Contain(
            "refresh the session first",
            "a procedure that renders its task and drops its outcome says 'you have done this before' "
            + "and nothing about how");
    }

    [Fact]
    public void AnUnrecordedOutcomeIsNotRenderedAsFailure()
    {
        // Success is bool? and null means UNRECORDED. Collapsing null into "failed" presents a
        // precedent library in which everything failed -- worse than showing nothing, because a wrong
        // precedent is acted on and an absent one is investigated.
        var result = new RecallResult
        {
            Context = new MemoryContext
            {
                SessionId = "s1",
                AssembledAtUtc = DateTimeOffset.UtcNow,
                SimilarTraces = new MemoryContextSection<ReasoningTrace>
                {
                    Items =
                    [
                        new ReasoningTrace
                        {
                            TraceId = "t1", SessionId = "s1", Task = "unknown outcome task",
                            Success = null, StartedAtUtc = DateTimeOffset.UtcNow,
                        },
                    ],
                },
            },
            TotalItemsRetrieved = 1,
        };

        var formatted = MemoryContextFormatter.FormatRecallResult(result);

        formatted.Should().Contain("[?]");
        formatted.Should().NotContain("[✗]");
    }
}
