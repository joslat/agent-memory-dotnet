using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// K6. Is GraphRAG actually reachable when the harness asks for it?
/// </summary>
/// <remarks>
/// The first K6 run measured GraphRAG returning zero items across sixty questions and was one step
/// from reporting that as a property of the surface. It was not: the harness pinned
/// <c>BlendMode = MemoryOnly</c>, which the assembler documents as "GraphRAG suppressed even when
/// enabled" and checks <i>before</i> the budget. A non-zero <c>MaxGraphRagItems</c> alone retrieves
/// nothing.
/// <para>
/// Three separate things must line up before a single item can come back — the flag, the registered
/// source, and the blend mode — and two of them are unreachable through the paths a reader would
/// naturally check (K9). Each one is asserted here, so the next zero is evidence about the surface
/// rather than about the wiring.
/// </para>
/// </remarks>
public sealed class GraphRagWiringTests
{
    [Fact]
    public void AskingForGraphRagEnablesIt()
    {
        // K9: this cannot be done through the configureMemory action at all, so the profile replaces
        // the registered IOptions<MemoryOptions>. If that override ever stops winning over the open
        // generic, GraphRAG silently returns nothing again.
        Resolve(graphRagIndexName: "fact_embedding_idx")
            .GetRequiredService<IOptions<MemoryOptions>>().Value
            .EnableGraphRag.Should().BeTrue();
    }

    [Fact]
    public void AskingForGraphRagRegistersASource()
    {
        Resolve(graphRagIndexName: "fact_embedding_idx")
            .GetService<IGraphRagContextSource>().Should().NotBeNull();
    }

    [Fact]
    public void TheConfiguredIndexAndProjectionSurvive()
    {
        // K10: without an explicit retrieval query a Fact node has no `text` property, so the prompt
        // would receive the driver's dump of the whole node, embedding included.
        var options = Resolve(graphRagIndexName: "fact_embedding_idx")
            .GetRequiredService<IOptions<AgentMemory.Neo4j.Infrastructure.GraphRagOptions>>().Value;

        options.IndexName.Should().Be("fact_embedding_idx");
        options.RetrievalQuery.Should().Contain("fact_id");
    }

    [Fact]
    public void NotAskingForGraphRagLeavesEverythingOff()
    {
        // The default for every run this track has produced, and the state prior runs are comparable
        // against. Nothing about K6 may change it.
        var provider = Resolve(graphRagIndexName: null);

        provider.GetRequiredService<IOptions<MemoryOptions>>().Value.EnableGraphRag.Should().BeFalse();
        provider.GetService<IGraphRagContextSource>().Should().BeNull();
    }

    [Theory]
    [InlineData(0, RetrievalBlendMode.MemoryOnly)]
    [InlineData(5, RetrievalBlendMode.Blended)]
    public void TheBlendModeStopsSuppressingGraphRagOnlyWhenABudgetIsAskedFor(
        int graphRagBudget, RetrievalBlendMode expected)
    {
        // The defect the first K6 run actually hit. MemoryOnly is checked before the budget, so this
        // is what decides whether any of the wiring above matters.
        AgentMemoryLongMemEvalAdapter.BlendModeFor(graphRagBudget).Should().Be(expected);
    }

    private static ServiceProvider Resolve(
        string? graphRagIndexName,
        bool resolveTemporalQueries = false,
        bool rescueShortOwnerResults = false) =>
        LongMemEvalMemoryProfile.ConfigureServices(
                "bolt://localhost:7687",
                Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
                Substitute.For<IChatClient>(),
                LongMemEvalMemoryMode.Structured,
                "gpt-4o-mini",
                embeddingDimensions: 1536,
                enableBatchedPreparation: true,
                maxConcurrentBatchesPerExtraction: 1,
                maxConcurrentExtractionBatches: 6,
                usePredicateVocabulary: true,
                // Named, so a future parameter inserted here cannot silently rebind this argument.
                assistantContent: AssistantContentMode.Ignore,
                resolveTemporalQueries: resolveTemporalQueries,
                rescueShortOwnerResults: rescueShortOwnerResults,
                graphRagIndexName: graphRagIndexName)
            .BuildServiceProvider();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTemporalResolutionFlagReachesMemoryOptions(bool enabled)
    {
        // 13.3's harness half, guarded here rather than by paying for a run. A flag parsed, threaded
        // through four signatures and then dropped before MemoryOptions is the exact shape that made
        // IncludeQuestionTypes and AbstentionPolicy dead options -- both shipped, both wired to
        // nothing, both discovered only when a measurement failed to move.
        using var provider = Resolve(graphRagIndexName: null, resolveTemporalQueries: enabled);

        provider.GetRequiredService<IOptions<MemoryOptions>>().Value
            .ResolveTemporalQueries.Should().Be(enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheRescueShortOwnerResultsFlagReachesMemoryOptions(bool enabled)
    {
        // 22.4. This lever had ZERO harness references: the one option aimed squarely at "a short
        // scoped result falls back to a bounded scan" could not be set from the benchmark at all --
        // so the mechanism most directly matching the measured failure mode (coverage, worth 80
        // points) was the single thing no run could exercise.
        using var provider = Resolve(graphRagIndexName: null, rescueShortOwnerResults: enabled);

        provider.GetRequiredService<IOptions<MemoryOptions>>().Value
            .RescueShortOwnerResults.Should().Be(enabled);
    }
}
