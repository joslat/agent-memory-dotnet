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
/// The relations a question resolved to must reach the caller, not be computed and discarded.
/// </summary>
/// <remarks>
/// Resolution was performed inline inside the fact-search call and thrown away, so no report could
/// distinguish "predicate expansion had nothing to expand" from "expansion ran and did not help".
/// Those need opposite responses — a missing vocabulary entry versus a retrieval or reading problem.
/// <para>
/// The distinction is not theoretical: on the n=50 losses, <c>service</c>/<c>serviced</c> turned out
/// to be absent from the table entirely, and <c>has</c> is a deliberate query stop form. Both
/// questions failed with expansion enabled and nothing to expand, and both looked exactly like an
/// ordinary retrieval miss.
/// </para>
/// </remarks>
public sealed class ResolvedQueryRelationsTests
{
    [Fact]
    public async Task AResolvableQuestionReportsItsRelations()
    {
        var context = await AssembleAsync("What did I buy last week?", resolve: true)
            .ConfigureAwait(true);

        context.ResolvedQueryRelations.Should().Contain("bought");
    }

    [Fact]
    public async Task AQuestionWithNoKnownRelationReportsNothingToExpand()
    {
        // The load-bearing case. Empty here means "expansion had nothing", which is the signal that
        // separates a vocabulary gap from a retrieval failure.
        var context = await AssembleAsync("How many bikes did I service in March?", resolve: true)
            .ConfigureAwait(true);

        context.ResolvedQueryRelations.Should().BeEmpty();
    }

    [Fact]
    public async Task ResolutionOffReportsNothing()
    {
        var context = await AssembleAsync("What did I buy last week?", resolve: false)
            .ConfigureAwait(true);

        context.ResolvedQueryRelations.Should().BeEmpty();
    }

    private static async Task<MemoryContext> AssembleAsync(string query, bool resolve)
    {
        var longTerm = Substitute.For<ILongTermMemoryService>();
        longTerm.SearchFactsAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<MemoryScope?>(),
                Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>([]));

        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        embeddings.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[8]));

        var assembler = new MemoryContextAssembler(
            Substitute.For<IShortTermMemoryService>(),
            longTerm,
            Substitute.For<IReasoningMemoryService>(),
            null,
            embeddings,
            Substitute.For<IClock>(),
            Options.Create(new MemoryOptions()),
            NullLogger<MemoryContextAssembler>.Instance,
            new DefaultMemoryIsolationPolicy(
                Options.Create(new MemoryIsolationOptions()),
                NullLogger<DefaultMemoryIsolationPolicy>.Instance));

        return await assembler.AssembleContextAsync(
                new RecallRequest
                {
                    SessionId = "s",
                    Query = query,
                    Options = new RecallOptions
                    {
                        MaxFacts = 10,
                        ExpandFactsByPredicate = true,
                        ResolveQueryRelations = resolve,
                    }
                })
            .ConfigureAwait(true);
    }
}
