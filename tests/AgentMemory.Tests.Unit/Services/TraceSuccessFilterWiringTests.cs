using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// K5. Automatic recall could surface the reasoning of a trace that FAILED, and present it as
/// precedent with nothing marking it as a failure.
/// </summary>
/// <remarks>
/// The capability was already there and unreachable: <c>SearchSimilarTracesAsync</c> takes a
/// <c>bool? successFilter</c>, the Cypher honours it as <c>node.success = $successFilter</c>, and the
/// assembler passed a hardcoded <c>null</c>. That is the dead-option shape this repository has fixed
/// before — an option built, plumbed, and never set by anything.
/// <para>
/// Upstream <c>neo4j-labs/agent-memory</c> defaults its equivalent to <c>success_only=True</c>,
/// treating it as a correctness property rather than a tuning knob: imitating reasoning that did not
/// work is worse than retrieving nothing. Our default is left at today's behaviour because nothing
/// here becomes a default before it is measured, and traces have never been measured.
/// </para>
/// </remarks>
public sealed class TraceSuccessFilterWiringTests
{
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();

    [Fact]
    public async Task TheSuccessFilterIsPassedThroughWhenRequested()
    {
        await AssembleAsync(new RecallOptions { MaxTraces = 5, SuccessfulTracesOnly = true })
            .ConfigureAwait(true);

        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), true, Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheDefaultIsUnchangedFromTodaysBehaviour()
    {
        // Byte-identical to the current call. Nothing becomes a default before it is measured, and
        // the trace surface has never been measured at all.
        await AssembleAsync(new RecallOptions { MaxTraces = 5 }).ConfigureAwait(true);

        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), null, Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FailedTracesCanBeRequestedExplicitlyForDiagnostics()
    {
        // Retrieving failures on purpose is a legitimate diagnostic; retrieving them by accident and
        // presenting them as precedent is the defect.
        await AssembleAsync(new RecallOptions { MaxTraces = 5, SuccessfulTracesOnly = false })
            .ConfigureAwait(true);

        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), false, Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    private async Task AssembleAsync(RecallOptions options)
    {
        _reasoning
            .SearchSimilarTracesAsync(
                Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>([]));

        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));

        var assembler = new MemoryContextAssembler(
            Substitute.For<IShortTermMemoryService>(),
            Substitute.For<ILongTermMemoryService>(),
            _reasoning,
            graphRag: null,
            embeddings,
            Substitute.For<IClock>(),
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

        await assembler.AssembleContextAsync(
                new RecallRequest { SessionId = "s", Query = "q", Options = options })
            .ConfigureAwait(true);
    }
}
