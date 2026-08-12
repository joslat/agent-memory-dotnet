using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// A trace whose outcome was never recorded must not be shown to the model as a failure.
/// </summary>
/// <remarks>
/// <para>
/// <c>ReasoningTrace.Success</c> is <c>bool?</c> and null means <b>unrecorded</b>. Null is also the
/// common case: <c>AgentTraceRecorder</c> had no success parameter at all until recently, so every
/// trace it wrote carries null.
/// </para>
/// <para>
/// Rendering that as <c>✗</c> presented the model with a precedent library in which everything had
/// failed — which is worse than showing nothing at all. <b>A wrong precedent is acted on; an absent
/// one is investigated.</b>
/// </para>
/// </remarks>
public sealed class TraceOutcomeRenderingTests
{
    private readonly ILongTermMemoryService _longTerm = Substitute.For<ILongTermMemoryService>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();
    private readonly IEmbeddingOrchestrator _embeddings = Substitute.For<IEmbeddingOrchestrator>();

    private static ReasoningTrace Trace(string id, bool? success) => new()
    {
        TraceId = id,
        SessionId = "s1",
        Task = $"task-{id}",
        Outcome = $"outcome-{id}",
        Success = success,
        StartedAtUtc = DateTimeOffset.UnixEpoch,
    };

    private MemoryQueryFacade CreateSut(params ReasoningTrace[] traces)
    {
        _embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[] { 0.1f }));
        _reasoning.SearchSimilarTracesAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(traces));

        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);

        return new MemoryQueryFacade(
            _longTerm, _reasoning, _embeddings, clock,
            Substitute.For<IIdGenerator>(),
            NullLogger<MemoryQueryFacade>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));
    }

    [Fact]
    public async Task AnUnrecordedOutcomeRendersAsUnknown_NotAsFailure()
    {
        var result = await CreateSut(Trace("a", success: null)).FindSimilarTasksAsync("anything");

        result.Text.Should().Contain("[?]");
        result.Text.Should().NotContain("[✗]",
            "an unrecorded outcome is not a failed one, and every MAF-recorded trace carries null");
    }

    [Fact]
    public async Task ARealSuccessAndARealFailureStillRenderDistinctly()
    {
        // The fix must not collapse the two states that ARE recorded.
        var result = await CreateSut(
            Trace("ok", success: true),
            Trace("bad", success: false),
            Trace("unknown", success: null)).FindSimilarTasksAsync("anything");

        result.Text.Should().Contain("[✓] task-ok");
        result.Text.Should().Contain("[✗] task-bad");
        result.Text.Should().Contain("[?] task-unknown");
    }

    [Fact]
    public async Task NoTracesStillReportsNothingFound()
    {
        var result = await CreateSut().FindSimilarTasksAsync("anything");

        result.Text.Should().Contain("No similar tasks found");
    }
}
