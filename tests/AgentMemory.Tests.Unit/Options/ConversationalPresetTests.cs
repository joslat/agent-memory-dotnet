using AgentMemory.Abstractions.Options;
using AgentMemory.AgentFramework;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.OptionsTests;

/// <summary>
/// PLAN 40.8. The Conversational preset's contents, pinned: a change to what it switches on must change this test,
/// because the preset is frozen per major version.
/// </summary>
public sealed class ConversationalPresetTests
{
    [Fact]
    public void The_memory_half_switches_on_exactly_what_it_documents()
    {
        var options = MemoryOptions.CreateConversational();

        options.Recall.MaxRelationships.Should().Be(5);
        options.ResolveTemporalQueries.Should().BeTrue();
        options.FanOut.Enabled.Should().BeTrue();
        options.Isolation.Mode.Should().Be(MemoryIsolationMode.StrictMultiTenant);
        options.WorkingMemory.RecentTopicsDays.Should().Be(7);
        options.EmbeddingCacheCapacity.Should().Be(512);
        options.UseAccessTrackingQueue.Should().BeTrue();
        options.SkipEscalationWhenOwnerHasNoRows.Should().BeTrue();

        var extraction = options.Extraction;
        extraction.SupersedeReplacedFacts.Should().BeTrue();
        extraction.RenameOnCorrectedName.Should().BeTrue();
        extraction.CanonicalFactSubjects.Should().BeTrue();
        extraction.DeduplicateWithinExtraction.Should().BeTrue();
        extraction.LinkFactsToEntities.Should().BeTrue();
        extraction.SkipPlainQuestions.Should().BeTrue();
        extraction.SkipUninformativeTurns.Should().BeTrue();
        extraction.EntityResolution.EnablePartialNameMatch.Should().BeTrue();
        extraction.EntityResolution.TypeStrictFiltering.Should().BeFalse();
    }

    /// <summary>
    /// Recall defaults to a process-wide singleton; a preset that wrote into it would change every other consumer's
    /// defaults. It must leave the defaults exactly as they were.
    /// </summary>
    [Fact]
    public void The_preset_leaves_the_defaults_untouched()
    {
        _ = MemoryOptions.CreateConversational();
        var defaults = new MemoryOptions();

        RecallOptions.Default.MaxRelationships.Should().Be(0);
        defaults.Recall.MaxRelationships.Should().Be(0);
        defaults.FanOut.Enabled.Should().BeFalse();
        defaults.Isolation.Mode.Should().Be(MemoryIsolationMode.SingleTenant);
        defaults.Extraction.SupersedeReplacedFacts.Should().BeFalse();
        MemoryOptions.CreateConversational().Extraction.Should().NotBeSameAs(MemoryOptions.CreateConversational().Extraction);
    }

    [Fact]
    public void The_extraction_half_switches_on_exactly_what_it_documents()
    {
        var options = new LlmExtractionOptions();

        options.ApplyConversational().Should().BeSameAs(options, "it composes inside a configure lambda");
        options.TemporalValidity.Should().Be(TemporalValidityMode.Extract);
        options.MarkCorrections.Should().BeTrue();
        options.OwnPreferencesOnly.Should().BeTrue();
        options.CaptureEventCompanions.Should().BeTrue();
        options.UseUnifiedExtraction.Should().BeTrue();
    }

    [Fact]
    public void The_agent_framework_half_switches_on_exactly_what_it_documents()
    {
        var options = new AgentFrameworkOptions();

        options.ApplyConversational().Should().BeSameAs(options);
        options.DeferQuestionTurns.Should().BeTrue();
    }
}
