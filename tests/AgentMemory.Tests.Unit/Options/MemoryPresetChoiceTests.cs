using AgentMemory;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using AgentMemory.Extraction.Llm;
using AgentMemory.Gate;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// Recommended is the default preset, and Conversational is its fallback when no retrieval judge is configured: the
/// judge is what makes Recommended worth running. The choice says why, in the memory tiers line.
/// </summary>
public sealed class MemoryPresetChoiceTests
{
    private static IConfiguration Configuration(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static readonly (string, string) Judge = ("AgentMemory:RetrievalRouter:Judges:0:Endpoint", "https://api.typesafe.ai/v1/systemone");

    [Fact]
    public void NothingConfiguredAsksForRecommended()
    {
        var choice = AgentMemoryPresets.Choose(Configuration(Judge));

        choice.Requested.Should().Be(MemoryPreset.Recommended);
        choice.Effective.Should().Be(MemoryPreset.Recommended);
        choice.UsesJudge.Should().BeTrue();
        choice.FellBack.Should().BeFalse();
    }

    [Fact]
    public void RecommendedWithoutAJudgeRunsAsConversationalAndSaysWhy()
    {
        var choice = AgentMemoryPresets.Choose(Configuration());

        choice.Requested.Should().Be(MemoryPreset.Recommended);
        choice.Effective.Should().Be(MemoryPreset.Conversational);
        choice.FellBack.Should().BeTrue();
        choice.UsesJudge.Should().BeFalse();
        choice.Reason.Should().Contain("retrieval judge").And.Contain("Conversational");
    }

    [Fact]
    public void AJudgeWithoutAnEndpointIsNotAJudge() =>
        AgentMemoryPresets.Choose(Configuration(("AgentMemory:RetrievalRouter:Judges:0:Name", "jev")))
            .Effective.Should().Be(MemoryPreset.Conversational);

    [Theory]
    [InlineData("Conversational", MemoryPreset.Conversational)]
    [InlineData("conversational", MemoryPreset.Conversational)]
    [InlineData("LowLevel", MemoryPreset.LowLevel)]
    public void AConfiguredPresetIsTakenAsIs(string named, MemoryPreset expected)
    {
        var choice = AgentMemoryPresets.Choose(Configuration(("AgentMemory:Preset", named), Judge));

        choice.Effective.Should().Be(expected);
        choice.FellBack.Should().BeFalse();
    }

    [Fact]
    public void AnUnknownPresetIsRefusedByName() =>
        FluentActions.Invoking(() => AgentMemoryPresets.Choose(Configuration(("AgentMemory:Preset", "Turbo"))))
            .Should().Throw<InvalidOperationException>().WithMessage("*Turbo*Recommended, Conversational or LowLevel*");

    [Fact]
    public void EachPresetBuildsItsOwnMemoryHalf()
    {
        MemoryPresetChoice.Decide(MemoryPreset.Recommended, 1).CreateMemoryOptions()
            .Should().BeEquivalentTo(MemoryOptions.CreateRecommended());
        MemoryPresetChoice.Decide(MemoryPreset.Recommended, 0).CreateMemoryOptions()
            .Should().BeEquivalentTo(MemoryOptions.CreateConversational());
        MemoryPresetChoice.Decide(MemoryPreset.LowLevel, 0).CreateMemoryOptions()
            .Should().BeEquivalentTo(new MemoryOptions());
    }

    [Fact]
    public void TheOtherHalvesFollowTheEffectivePreset()
    {
        new LlmExtractionOptions().ApplyPreset(MemoryPreset.Recommended)
            .Should().BeEquivalentTo(new LlmExtractionOptions().ApplyRecommended());
        new LlmExtractionOptions().ApplyPreset(MemoryPreset.Conversational)
            .Should().BeEquivalentTo(new LlmExtractionOptions().ApplyConversational());
        new LlmExtractionOptions().ApplyPreset(MemoryPreset.LowLevel)
            .Should().BeEquivalentTo(new LlmExtractionOptions());
        new AgentFrameworkOptions().ApplyPreset(MemoryPreset.Conversational).DeferQuestionTurns.Should().BeTrue();
        new AgentFrameworkOptions().ApplyPreset(MemoryPreset.Recommended).DeferQuestionTurns.Should().BeFalse();
    }

    [Fact]
    public void TheGateIsRegisteredOnlyWhenThePresetRunsAJudge()
    {
        var conversational = new ServiceCollection();
        conversational.AddAgentMemoryGate(MemoryPresetChoice.Decide(MemoryPreset.Recommended, 0), Configuration());
        conversational.Should().BeEmpty("Conversational recalls exactly as without the gate");
    }

    [Fact]
    public void TheRegistrationReportsThePresetInTheTiersLine()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IChatClient>());
        services.AddSingleton(Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>());
        services.AddNeo4jAgentMemory(MemoryPresetChoice.Decide(MemoryPreset.Recommended, 0), neo4j =>
        {
            neo4j.Uri = "bolt://localhost:7687";
            neo4j.Password = "unused";
        });
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<MemoryPresetChoice>().Effective.Should().Be(MemoryPreset.Conversational);
        var tiers = provider.GetServices<IMemoryTierSource>().Select(source => source.Describe()).ToList();
        tiers.Should().ContainSingle(t => t.Leg == "preset")
            .Which.Should().Match<MemoryTier>(t => t.Tier == "Conversational" && t.Note!.Contains("retrieval judge"));
    }
}
