using AgentMemory.Abstractions.Options;
using AgentMemory.AgentFramework;
using AgentMemory.Extraction.Llm;
using AgentMemory.Gate;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// AMREC001. The Recommended preset's contents, pinned: the Conversational preset plus what the storage and retrieval
/// research measured (the memory configuration the store-aware writer was measured with, the writer, the update judge and
/// the retrieval memory router). Its contents may change before 2.0; a change must change this test.
/// </summary>
public sealed class RecommendedPresetTests
{
    [Fact]
    public void The_memory_half_is_the_conversational_preset_with_bitemporal_changes()
    {
        var recommended = MemoryOptions.CreateRecommended();
        var conversational = MemoryOptions.CreateConversational();

        recommended.Extraction.BitemporalChanges.Should().BeTrue();
        recommended.Should().BeEquivalentTo(conversational, o => o.Excluding(x => x.Extraction.BitemporalChanges),
            "everything else is the Conversational preset");
    }

    [Fact]
    public void The_preset_leaves_the_conversational_preset_and_the_defaults_untouched()
    {
        _ = MemoryOptions.CreateRecommended();

        MemoryOptions.CreateConversational().Extraction.BitemporalChanges.Should().BeFalse("the Conversational preset is frozen");
        new MemoryOptions().Extraction.BitemporalChanges.Should().BeFalse();
        new MemoryOptions().Isolation.Mode.Should().Be(MemoryIsolationMode.SingleTenant);
        MemoryOptions.CreateRecommended().Extraction.Should().NotBeSameAs(MemoryOptions.CreateRecommended().Extraction);
    }

    [Fact]
    public void The_extraction_half_is_the_conversational_half_with_the_store_aware_writer()
    {
        var options = new LlmExtractionOptions();

        options.ApplyRecommended().Should().BeSameAs(options, "it composes inside a configure lambda");
        options.UseMemoryWriter.Should().BeTrue();
        options.ChatModelUpdateJudge.Should().BeTrue("the writer's closings are confirmed by the host's chat model when no outside judge answers (41.26 c)");
        options.Should().BeEquivalentTo(new LlmExtractionOptions().ApplyConversational(),
            o => o.Excluding(x => x.UseMemoryWriter).Excluding(x => x.ChatModelUpdateJudge),
            "the windows the writer does not take go to the extractors as the Conversational preset configures them");
        new LlmExtractionOptions().ApplyConversational().UseMemoryWriter.Should().BeFalse();
        new LlmExtractionOptions().ApplyConversational().ChatModelUpdateJudge.Should().BeFalse();
    }

    [Fact]
    public void The_gate_half_turns_on_the_judge_for_recall_and_for_the_write_path_but_adds_no_judge()
    {
        var options = new MemoryGateOptions();

        options.ApplyRecommended().Should().BeSameAs(options);
        options.Mode.Should().Be(MemoryGateMode.Judge);
        options.UpdateJudge.Should().BeTrue();
        options.Judges.Should().BeEmpty("the endpoint and its key are the host's to configure");
        options.Threshold.Should().Be(new MemoryGateOptions().Threshold);
    }

    /// <summary>
    /// The writer takes a window of exactly one user message; held question turns are released with the next turn as one
    /// window of two, which the extractors write instead. The preset has no Agent Framework half that would hold them.
    /// </summary>
    [Fact]
    public void Question_turns_are_not_held_by_default()
    {
        new AgentFrameworkOptions().DeferQuestionTurns.Should().BeFalse();
    }
}
