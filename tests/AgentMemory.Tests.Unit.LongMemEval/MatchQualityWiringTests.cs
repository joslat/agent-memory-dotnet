using AgentMemory.Abstractions.Options;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// <c>--annotate-match-quality</c> must reach <c>MemoryProjectionOptions</c>, and leave the default
/// alone when it is off.
/// </summary>
/// <remarks>
/// <para>
/// <c>AnnotateMatchQuality</c> is the memory-layer half of an abstention. It renders how well each
/// recalled item matched, and says so when nothing did. No run could set it: it is the same
/// reachable-but-never-fed shape as the supersession renderer before 30.9d.
/// </para>
/// <para>
/// Off must stay reference-identical to <c>MemoryProjectionOptions.Default</c>, because the assembler
/// compares that reference to tell "unset" from "set to the defaults". A copy in the off state would change
/// the path of every sealed measurement.
/// </para>
/// </remarks>
public sealed class MatchQualityWiringTests
{
    [Fact]
    public void TheFlagReachesTheProjectionOptions()
    {
        using var provider = Resolve(annotateMatchQuality: true);

        var projection = provider.GetRequiredService<IOptions<MemoryOptions>>().Value.Projection;
        projection.AnnotateMatchQuality.Should().BeTrue();
        projection.ResolveSupersessions.Should().BeFalse("one lever must not switch on another");
    }

    [Fact]
    public void OffKeepsTheSharedDefaultInstance()
    {
        using var provider = Resolve(annotateMatchQuality: false);

        provider.GetRequiredService<IOptions<MemoryOptions>>().Value.Projection
            .Should().BeSameAs(MemoryProjectionOptions.Default);
    }

    [Fact]
    public void ItComposesWithTheSupersessionRenderer()
    {
        using var provider = Resolve(annotateMatchQuality: true, resolveSupersessions: true);

        var projection = provider.GetRequiredService<IOptions<MemoryOptions>>().Value.Projection;
        projection.AnnotateMatchQuality.Should().BeTrue();
        projection.ResolveSupersessions.Should().BeTrue();
    }

    [Fact]
    public void ThePreparedPairParsesTheFlagAndDefaultsItOff()
    {
        LongMemEvalPreparedPairProgram.Parse(["--prepared-pair", "--annotate-match-quality"])
            .AnnotateMatchQuality.Should().BeTrue();
        LongMemEvalPreparedPairProgram.Parse(["--prepared-pair"])
            .AnnotateMatchQuality.Should().BeFalse();
    }

    /// <summary>
    /// Recall-side only: it must reach the evaluation profiles, and never the preparation base.
    /// </summary>
    /// <remarks>
    /// That split is what lets a later run reuse the same sealed store with the annotation on. The
    /// prepared pair starts exactly two kinds of profile, the base (one call site) and each evaluation arm
    /// (the other), so the flag must appear in exactly one of them.
    /// </remarks>
    [Fact]
    public void OnlyTheEvaluationProfilesReceiveIt()
    {
        var source = ToolSource("LongMemEvalPreparedPairProgram.cs");

        source.Split("annotateMatchQuality: options.AnnotateMatchQuality").Length.Should().Be(2);
        var armStart = source.IndexOf("private static async Task<PreparedArmExecution> RunArmAsync", StringComparison.Ordinal);
        source.IndexOf("annotateMatchQuality: options.AnnotateMatchQuality", StringComparison.Ordinal)
            .Should().BeGreaterThan(armStart);
    }

    private static ServiceProvider Resolve(bool annotateMatchQuality, bool resolveSupersessions = false) =>
        LongMemEvalMemoryProfile.ConfigureServices(
                "bolt://localhost:7687",
                Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
                Substitute.For<IChatClient>(),
                LongMemEvalMemoryMode.Structured,
                "test-model",
                embeddingDimensions: 1024,
                enableBatchedPreparation: false,
                maxConcurrentBatchesPerExtraction: 1,
                maxConcurrentExtractionBatches: 0,
                usePredicateVocabulary: false,
                assistantContent: AssistantContentMode.Ignore,
                resolveTemporalQueries: false,
                rescueShortOwnerResults: false,
                supersedeReplacedFacts: false,
                linkFactsToEntities: false,
                nodeDistanceReranking: false,
                temporalValidity: false,
                captureIdentityAliases: false,
                resolveSupersessions: resolveSupersessions,
                recallFanOut: false,
                phase30: PhaseThirtyFeatures.AllOff,
                graphRagIndexName: null,
                annotateMatchQuality: annotateMatchQuality)
            .BuildServiceProvider();

    private static string ToolSource(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(
            directory!.FullName, "tools", "AgentMemory.LongMemEval", fileName));
    }
}
