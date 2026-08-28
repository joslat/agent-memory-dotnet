using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extraction.Llm;
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
        bool rescueShortOwnerResults = false,
        bool supersedeReplacedFacts = false,
        bool resolveSupersessions = false,
        int? extractionSeed = null,
        PhaseThirtyFeatures? phase30 = null) =>
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
                supersedeReplacedFacts: supersedeReplacedFacts,
                resolveSupersessions: resolveSupersessions,
                phase30: phase30 ?? PhaseThirtyFeatures.AllOff,
                graphRagIndexName: graphRagIndexName,
                extractionSeed: extractionSeed)
            .BuildServiceProvider();

    /// <summary>
    /// 30.9c's harness half: the Wave-C switches must reach <c>MemoryOptions</c>, not merely parse.
    /// </summary>
    /// <remarks>
    /// Every Phase-30 Wave-C capability shipped off by default AND unreachable from the harness —
    /// the profile set three fields on <c>MemoryOptions</c> and none was a Phase-30 flag. So the
    /// features built to move the benchmark numbers were the one thing no run could exercise, and
    /// 30.6 sat "built, not measured" because it was <b>unmeasurable</b>. Same shape as the dead
    /// options this class already guards, one wave later.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheArithmeticMemoryFlagReachesMemoryOptions(bool enabled)
    {
        using var provider = Resolve(
            graphRagIndexName: null,
            phase30: new PhaseThirtyFeatures(ArithmeticMemory: enabled));

        provider.GetRequiredService<IOptions<MemoryOptions>>()
            .Value.Extraction.DerivedMemory.Enabled.Should().Be(enabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheWorkingMemoryFlagReachesMemoryOptions(bool enabled)
    {
        using var provider = Resolve(
            graphRagIndexName: null,
            phase30: new PhaseThirtyFeatures(WorkingMemory: enabled));

        provider.GetRequiredService<IOptions<MemoryOptions>>()
            .Value.WorkingMemory.Enabled.Should().Be(enabled);
    }

    /// <summary>
    /// 30.9d: the renderer flag must reach <c>MemoryOptions.Projection</c>, not merely parse.
    /// </summary>
    /// <remarks>
    /// The SECOND reachable-but-never-fed lever inside one intervention.
    /// <c>SupersessionProjectionFeature</c> was fully built and gated on
    /// <c>ResolveSupersessions</c>, which defaults false and which no harness ever set — so every
    /// scored run rendered supersession chains dark, including the ablation that existed to measure
    /// supersession. Same shape as the arithmetic flag above, one wave later.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheResolveSupersessionsFlagReachesMemoryOptions(bool enabled)
    {
        using var provider = Resolve(graphRagIndexName: null, resolveSupersessions: enabled);

        provider.GetRequiredService<IOptions<MemoryOptions>>()
            .Value.Projection.ResolveSupersessions.Should().Be(enabled);
    }

    /// <summary>
    /// Off must leave <c>Projection</c> REFERENCE-identical to the shared default.
    /// </summary>
    /// <remarks>
    /// <c>MemoryProjectionOptions.Default</c> is reference-compared to tell "unset" from "set to the
    /// defaults" (<c>MemoryContextAssembler.ResolveProjectionOptions</c>), so assigning a
    /// <c>with</c>-copy unconditionally would change identity for every sealed measurement while
    /// leaving all property values equal — a behavioural change that property assertions cannot see.
    /// This is the test that fails if the conditional assignment is ever "simplified" away.
    /// </remarks>
    [Fact]
    public void TheOffStateKeepsTheSharedProjectionDefaultInstance()
    {
        using var provider = Resolve(graphRagIndexName: null, resolveSupersessions: false);

        provider.GetRequiredService<IOptions<MemoryOptions>>()
            .Value.Projection.Should().BeSameAs(MemoryProjectionOptions.Default);
    }

    /// <summary>
    /// Turning rendering on must not silently retune the chain depth the arm did not ask about.
    /// </summary>
    [Fact]
    public void TurningRenderingOnLeavesTheChainDepthAtItsDefault()
    {
        using var provider = Resolve(graphRagIndexName: null, resolveSupersessions: true);

        provider.GetRequiredService<IOptions<MemoryOptions>>()
            .Value.Projection.MaxSupersessionChain.Should()
            .Be(MemoryProjectionOptions.Default.MaxSupersessionChain);
    }

    /// <summary>
    /// A flag without its schema extension is worse than a no-op: the DDL those writes need would be
    /// absent, so they fail at the store and the feature reads as broken rather than dark.
    /// </summary>
    [Fact]
    public void EnablingAFeatureInstallsItsSchemaExtension()
    {
        using var provider = Resolve(
            graphRagIndexName: null,
            phase30: new PhaseThirtyFeatures(ArithmeticMemory: true));

        provider.GetRequiredService<IOptions<AgentMemory.Neo4j.Infrastructure.Neo4jOptions>>()
            .Value.Extensions.Should().Contain("arithmetic");
    }

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

    [Theory]
    [InlineData(null)]
    [InlineData(20260815)]
    public void TheExtractionSeedReachesTheExtractorOptions(int? seed)
    {
        // 30.1. LlmExtractionOptions.Seed shipped with NO writer anywhere in the harness: three cold
        // builds of one configuration stored 6,078 / 6,199 / 6,272 canonical triples with 7.5% common
        // to all three, and the single option the provider offers against that could not be set from
        // the benchmark at all. This is the same shape as RescueShortOwnerResults above -- an option
        // parsed, threaded and then dropped one line before it mattered -- so it is guarded here rather
        // than discovered when a seeded build turns out to have been unseeded.
        using var provider = Resolve(graphRagIndexName: null, extractionSeed: seed);

        provider.GetRequiredService<IOptions<LlmExtractionOptions>>().Value.Seed.Should().Be(seed);
    }

    [Fact]
    public void NotAskingForASeedSendsNone()
    {
        // The state every sealed measurement was taken under. Null sends no seed field at all, so a
        // corpus built without --extraction-seed is byte-identical in its requests to every prior one.
        using var provider = Resolve(graphRagIndexName: null);

        provider.GetRequiredService<IOptions<LlmExtractionOptions>>().Value.Seed.Should().BeNull();
    }
}
