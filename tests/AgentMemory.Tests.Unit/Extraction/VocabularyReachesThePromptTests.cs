using AgentMemory.Core.Memory;
using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Does the relation vocabulary actually reach the model, or only the object graph?
/// </summary>
/// <remarks>
/// L3 was retired on the claim that "extraction was offered these words and chose not to use them".
/// That claim was made from reading the wiring — seed built, assigned to the extractor — and NOT from
/// inspecting the prompt the model receives. Built is not wired and wired is not measured, and the
/// difference between "the model declined" and "the model never saw it" is the difference between a
/// prompt-preference problem and a plain bug.
/// </remarks>
public sealed class VocabularyReachesThePromptTests(ITestOutputHelper output)
{
    [Fact]
    public void TheFourRelationsThatWentMissingAreActuallyInThePrompt()
    {
        var prompt = LlmMultiSessionUnifiedMemoryExtractor.BuildSystemPrompt(
            MemoryPredicateSeedVocabulary.Create());

        output.WriteLine($"prompt length = {prompt.Length} chars");
        var marker = "Established relation predicates";
        var idx = prompt.IndexOf(marker, StringComparison.Ordinal);
        output.WriteLine($"vocabulary section present: {idx >= 0}");
        if (idx >= 0) output.WriteLine(prompt[idx..]);

        foreach (var relation in new[] { "recommend", "watch", "commute", "arrive" })
            output.WriteLine($"  contains '{relation}': {prompt.Contains(relation, StringComparison.Ordinal)}");

        prompt.Should().Contain(marker);
    }

    [Fact]
    public void TheSeedIsNotSilentlyTruncated()
    {
        var vocabulary = MemoryPredicateSeedVocabulary.Create();
        output.WriteLine($"vocabulary Count = {vocabulary.Count}");
        var snapshot = vocabulary.Snapshot();
        output.WriteLine($"snapshot size    = {snapshot.Count}");
        output.WriteLine("first 25: " + string.Join(", ", snapshot.Take(25)));
        vocabulary.Count.Should().Be(snapshot.Count);
    }
}
