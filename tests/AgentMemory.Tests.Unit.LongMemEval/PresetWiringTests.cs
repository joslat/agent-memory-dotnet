using AgentMemory.Abstractions.Options;
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
/// PLAN 40.10. <c>--preset</c> measures what a user gets: the shipped defaults, or those plus the Conversational preset
/// object. Without it, the sealed profile is untouched.
/// </summary>
public sealed class PresetWiringTests
{
    [Theory]
    [InlineData(null, "sealed")]
    [InlineData("sealed", "sealed")]
    [InlineData("defaults", "defaults")]
    [InlineData("Conversational", "conversational")]
    public void The_preset_option_parses(string? value, string expected)
    {
        LongMemEvalPresets.Token(LongMemEvalPresets.Parse(value)).Should().Be(expected);
    }

    [Fact]
    public void An_unknown_preset_is_refused()
    {
        var act = () => LongMemEvalPresets.Parse("everything");

        act.Should().Throw<ArgumentException>();
    }

    /// <summary>A store built under one configuration is never reused under another.</summary>
    [Fact]
    public void A_preset_is_sealed_into_the_extraction_identity()
    {
        LongMemEvalPresets.Seal("zai-org/GLM-5.3-Flash@bitdeer", LongMemEvalPreset.Sealed).Should().Be("zai-org/GLM-5.3-Flash@bitdeer");
        LongMemEvalPresets.Seal("zai-org/GLM-5.3-Flash@bitdeer", LongMemEvalPreset.Conversational)
            .Should().Be("zai-org/GLM-5.3-Flash@bitdeer+preset:conversational");
        LongMemEvalPresets.Seal("m", LongMemEvalPreset.Defaults).Should().NotBe(LongMemEvalPresets.Seal("m", LongMemEvalPreset.Conversational));
    }

    [Fact]
    public void Conversational_reaches_memory_and_extraction_options_through_the_preset_objects()
    {
        using var provider = Resolve(LongMemEvalPreset.Conversational, enableBatchedPreparation: true);

        var memory = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;
        memory.FanOut.Enabled.Should().BeTrue();
        memory.Isolation.Mode.Should().Be(MemoryIsolationMode.StrictMultiTenant);
        memory.Recall.MaxRelationships.Should().Be(5);
        memory.Extraction.SupersedeReplacedFacts.Should().BeTrue();
        memory.OwnerFirstVectorThreshold.Should().Be(new MemoryOptions().OwnerFirstVectorThreshold, "the product default, not the sealed pin");

        var llm = provider.GetRequiredService<IOptions<LlmExtractionOptions>>().Value;
        llm.TemporalValidity.Should().Be(TemporalValidityMode.Extract);
        llm.MarkCorrections.Should().BeTrue();
        llm.CaptureEventCompanions.Should().BeTrue();
        llm.UseMultiSessionBatchExtraction.Should().BeTrue("the harness still selects its own extractor");
    }

    [Fact]
    public void Defaults_measure_the_shipped_defaults_not_the_sealed_pins()
    {
        using var provider = Resolve(LongMemEvalPreset.Defaults, enableBatchedPreparation: false);

        var memory = provider.GetRequiredService<IOptions<MemoryOptions>>().Value;
        memory.OwnerFirstVectorThreshold.Should().Be(new MemoryOptions().OwnerFirstVectorThreshold);
        memory.FanOut.Enabled.Should().BeFalse();
        memory.Isolation.Mode.Should().Be(MemoryIsolationMode.SingleTenant);

        var llm = provider.GetRequiredService<IOptions<LlmExtractionOptions>>().Value;
        llm.CaptureUserName.Should().Be(new LlmExtractionOptions().CaptureUserName, "the shipped default, not the sealed pin");
        llm.UseUnifiedExtraction.Should().BeFalse("an evaluation profile prepares nothing, in every arm alike");
    }

    [Fact]
    public void Without_a_preset_the_sealed_pins_stay()
    {
        using var provider = Resolve(LongMemEvalPreset.Sealed, enableBatchedPreparation: false);

        provider.GetRequiredService<IOptions<MemoryOptions>>().Value.OwnerFirstVectorThreshold.Should().Be(0);
        provider.GetRequiredService<IOptions<LlmExtractionOptions>>().Value.CaptureUserName.Should().BeFalse();
    }

    [Fact]
    public void The_report_says_what_of_the_preset_a_prepared_pair_cannot_exercise()
    {
        var coverage = LongMemEvalPresets.Coverage(LongMemEvalPreset.Conversational);
        var notExercised = (string[])coverage.GetType().GetProperty("notExercised")!.GetValue(coverage)!;

        notExercised.Should().Contain(item => item.StartsWith("SkipPlainQuestions", StringComparison.Ordinal));
        notExercised.Should().Contain(item => item.StartsWith("DeferQuestionTurns", StringComparison.Ordinal));
    }

    private static ServiceProvider Resolve(LongMemEvalPreset preset, bool enableBatchedPreparation) =>
        LongMemEvalMemoryProfile.ConfigureServices(
                "bolt://localhost:7687",
                Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>(),
                Substitute.For<IChatClient>(),
                LongMemEvalMemoryMode.Structured,
                "test-model",
                embeddingDimensions: 1024,
                enableBatchedPreparation: enableBatchedPreparation,
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
                resolveSupersessions: false,
                recallFanOut: false,
                phase30: PhaseThirtyFeatures.AllOff,
                graphRagIndexName: null,
                preset: preset)
            .BuildServiceProvider();
}
