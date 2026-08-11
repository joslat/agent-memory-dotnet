using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// A completed trace must be able to say whether it succeeded.
/// </summary>
/// <remarks>
/// <c>IReasoningMemoryService.CompleteTraceAsync</c> has always accepted <c>bool? success</c>, and the
/// TCK bridge has always passed it. The MAF recorder had no way to, so every trace it persisted stored
/// <c>success = null</c> — and a null outcome is not neutral:
/// <list type="bullet">
/// <item><description><c>MemoryQueryFacade</c> renders a recalled trace as
/// <c>t.Success == true ? "✓" : "✗"</c>, so it reaches the model marked as a <b>failed</b>
/// precedent. Recalling a failure as if it were a lesson is worse than recalling nothing.</description></item>
/// <item><description><c>SuccessfulTracesOnly = true</c> filters on <c>node.success = $successFilter</c>,
/// and Cypher's <c>null = true</c> is null — so successful-only recall returned <b>nothing at
/// all</b>.</description></item>
/// </list>
/// </remarks>
public sealed class TraceOutcomeIsRecordedTests
{
    private static (AgentTraceRecorder Recorder, IReasoningMemoryService Service) Build()
    {
        var trace = new ReasoningTrace
        {
            TraceId = "t1",
            SessionId = "s1",
            Task = "task",
            StartedAtUtc = DateTimeOffset.UnixEpoch,
        };
        var service = Substitute.For<IReasoningMemoryService>();
        service.CompleteTraceAsync(
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(trace);

        var options = Options.Create(new AgentFrameworkOptions { PersistReasoningTraces = true });
        return (new AgentTraceRecorder(
            service,
            Substitute.For<IClock>(),
            Substitute.For<IIdGenerator>(),
            options,
            NullLogger<AgentTraceRecorder>.Instance), service);
    }

    [Fact]
    public async Task ASuccessfulTraceIsRecordedAsSuccessful()
    {
        var (recorder, service) = Build();

        await recorder.CompleteTraceAsync("t1", "done", success: true);

        await service.Received(1).CompleteTraceAsync("t1", "done", true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailedTraceIsRecordedAsFailed()
    {
        // Distinct from "unknown". A recorded failure is a usable signal; a null is a silent one.
        var (recorder, service) = Build();

        await recorder.CompleteTraceAsync("t1", "gave up", success: false);

        await service.Received(1).CompleteTraceAsync("t1", "gave up", false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheOverloadWithoutAnOutcomeStillRecordsNull()
    {
        // Source compatibility is preserved deliberately, and this is exactly the shape that produced
        // the defect — so it is pinned rather than left implicit.
        var (recorder, service) = Build();

        await recorder.CompleteTraceAsync("t1", "done");

        await service.Received(1).CompleteTraceAsync("t1", "done", null, Arg.Any<CancellationToken>());
    }
}
