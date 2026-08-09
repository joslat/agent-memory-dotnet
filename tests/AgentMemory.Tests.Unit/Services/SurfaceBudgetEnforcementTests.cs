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
/// K5. Verification, not repair: are the reasoning-trace and GraphRAG budgets actually enforced?
/// </summary>
/// <remarks>
/// These surfaces have carried a budget of zero in every quality measurement, so "zero" has never
/// been checked to mean zero. It matters more than it sounds, because
/// <c>Neo4jGraphRagContextSource</c> deliberately treats a <c>TopK</c> of 0 as "use the configured
/// default" rather than "return nothing" — pinned by its own test. The composed system is safe only
/// because the assembler skips the call entirely instead of passing zero through, which is a
/// property worth holding with a test rather than a comment.
/// <para>
/// A direct consumer of <see cref="IGraphRagContextSource"/> that asks for zero items still receives
/// the configured default. That is recorded as a finding rather than changed here: it is deliberate,
/// tested, and on a SemVer-locked public interface.
/// </para>
/// </remarks>
public sealed class SurfaceBudgetEnforcementTests
{
    private readonly IGraphRagContextSource _graphRag = Substitute.For<IGraphRagContextSource>();
    private readonly IReasoningMemoryService _reasoning = Substitute.For<IReasoningMemoryService>();

    [Fact]
    public async Task AZeroGraphRagBudgetReachesTheSourceAsNoCallAtAll()
    {
        // The load-bearing property. If the assembler passed zero through, the source would answer
        // with its configured default and a caller asking for nothing would get five passages.
        await AssembleAsync(new RecallOptions { MaxGraphRagItems = 0 }).ConfigureAwait(true);

        await _graphRag.DidNotReceive().GetContextAsync(
            Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANonZeroGraphRagBudgetIsPassedThroughVerbatim()
    {
        await AssembleAsync(new RecallOptions { MaxGraphRagItems = 3 }).ConfigureAwait(true);

        await _graphRag.Received(1).GetContextAsync(
            Arg.Is<GraphRagContextRequest>(request => request.TopK == 3),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AZeroTraceBudgetSkipsTheTraceSearch()
    {
        await AssembleAsync(new RecallOptions { MaxTraces = 0 }).ConfigureAwait(true);

        await _reasoning.DidNotReceive().SearchSimilarTracesAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ANonZeroTraceBudgetIsPassedThroughVerbatim()
    {
        await AssembleAsync(new RecallOptions { MaxTraces = 4 }).ConfigureAwait(true);

        await _reasoning.Received(1).SearchSimilarTracesAsync(
            Arg.Any<float[]>(), Arg.Any<bool?>(), 4, Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    private async Task<MemoryContext> AssembleAsync(RecallOptions options)
    {
        _graphRag.GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GraphRagContextResult { Items = [] }));
        _reasoning.SearchSimilarTracesAsync(
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
            _graphRag,
            embeddings,
            Substitute.For<IClock>(),
            Options.Create(new MemoryOptions { EnableGraphRag = true }),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

        return await assembler.AssembleContextAsync(
                new RecallRequest { SessionId = "s", Query = "q", Options = options })
            .ConfigureAwait(true);
    }
}
