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
/// K4. GraphRAG could not be scored, attributed, or shown to have helped, and the reason turned out
/// not to be the surface.
/// </summary>
/// <remarks>
/// Its budget has been zero in every quality measurement this track has produced, and K1 concluded
/// that was probably because it contributes one opaque prose string where every other surface
/// contributes typed items the evidence accounting can attribute.
/// <para>
/// But the items already exist. <c>GraphRagContextItem</c> carries <c>Text</c>, <c>Score</c>,
/// <c>SourceNodeIds</c> and <c>Metadata</c>; the assembler joined their text and threw the rest away.
/// The information was never missing - it was discarded one line before it could be used.
/// </para>
/// </remarks>
public sealed class GraphRagAttributionTests
{
    private readonly IGraphRagContextSource _graphRag = Substitute.For<IGraphRagContextSource>();

    [Fact]
    public async Task RetrievedItemsKeepTheirIdentityAndScore()
    {
        var context = await AssembleAsync().ConfigureAwait(true);

        context.GraphRagItems.Should().HaveCount(2);
        context.GraphRagItems[0].SourceNodeIds.Should().Contain("n-1");
        context.GraphRagItems[0].Score.Should().Be(0.91);
        context.GraphRagItems[1].SourceNodeIds.Should().Contain("n-2");
    }

    [Fact]
    public async Task ThePromptTextIsUnchanged()
    {
        // Attribution must be additive. The reader sees exactly what it saw before, or this becomes
        // a retrieval change masquerading as instrumentation.
        var context = await AssembleAsync().ConfigureAwait(true);

        context.GraphRagContext.Should().Be("first passage\n\nsecond passage");
    }

    [Fact]
    public async Task NoGraphRagMeansNoItemsRatherThanNull()
    {
        // An empty list, never null: a caller counting contributions must not need a null check to
        // distinguish "GraphRAG was off" from "GraphRAG returned nothing".
        var context = await AssembleAsync(withGraphRag: false).ConfigureAwait(true);

        context.GraphRagItems.Should().NotBeNull().And.BeEmpty();
        context.GraphRagContext.Should().BeNull();
    }

    private async Task<MemoryContext> AssembleAsync(bool withGraphRag = true)
    {
        _graphRag
            .GetContextAsync(Arg.Any<GraphRagContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new GraphRagContextResult
            {
                Items =
                [
                    new GraphRagContextItem
                    {
                        Text = "first passage", Score = 0.91, SourceNodeIds = ["n-1"]
                    },
                    new GraphRagContextItem
                    {
                        Text = "second passage", Score = 0.42, SourceNodeIds = ["n-2"]
                    }
                ]
            }));

        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));

        var assembler = new MemoryContextAssembler(
            Substitute.For<IShortTermMemoryService>(),
            Substitute.For<ILongTermMemoryService>(),
            Substitute.For<IReasoningMemoryService>(),
            withGraphRag ? _graphRag : null,
            embeddings,
            Substitute.For<IClock>(),
            Options.Create(new MemoryOptions { EnableGraphRag = withGraphRag }),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

        return await assembler.AssembleContextAsync(
                new RecallRequest
                {
                    SessionId = "s",
                    Query = "q",
                    Options = new RecallOptions { MaxGraphRagItems = 5 }
                })
            .ConfigureAwait(true);
    }
}
