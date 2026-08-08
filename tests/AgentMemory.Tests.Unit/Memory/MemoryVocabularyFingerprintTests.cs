using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// The extraction vocabulary decides what gets stored and the query lexicon decides what gets
/// retrieved, so two runs built or measured under different tables are not comparable. Nothing
/// recorded which table produced a given graph — the same defect as the retrieval flags that were
/// missing from the run fingerprint.
/// </summary>
public sealed class MemoryVocabularyFingerprintTests
{
    [Fact]
    public void TheFingerprintIsOrderIndependentBecauseAVocabularyIsASet()
    {
        // Authoring order is not meaning. If reordering the table changed the fingerprint, every
        // cosmetic edit would look like a vocabulary change and invalidate comparisons for nothing.
        MemoryVocabularyFingerprint.Of(["bought", "sold", "likes"]).Should()
            .Be(MemoryVocabularyFingerprint.Of(["likes", "bought", "sold"]));
    }

    [Fact]
    public void OneAddedEntryChangesTheFingerprint()
    {
        // The case that matters: adding `assembled` changes what the extractor will store, so a graph
        // built before it must never be mistaken for one built after.
        MemoryVocabularyFingerprint.Of(["bought", "sold"]).Should()
            .NotBe(MemoryVocabularyFingerprint.Of(["bought", "sold", "assembled"]));
    }

    [Fact]
    public void OneRemovedEntryChangesTheFingerprint() =>
        MemoryVocabularyFingerprint.Of(["bought", "sold", "likes"]).Should()
            .NotBe(MemoryVocabularyFingerprint.Of(["bought", "sold"]));

    [Fact]
    public void DuplicatesDoNotChangeTheFingerprint() =>
        MemoryVocabularyFingerprint.Of(["bought", "bought", "sold"]).Should()
            .Be(MemoryVocabularyFingerprint.Of(["bought", "sold"]));

    [Fact]
    public void TheFingerprintIsLowercaseHexAndFullLength() =>
        MemoryVocabularyFingerprint.Of(["bought"]).Should()
            .HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");

    [Fact]
    public void TheShippedVocabularyFingerprintsAreStable()
    {
        // They become recorded run metadata, so they must not drift between calls or processes.
        MemoryPredicateSeedVocabulary.Fingerprint.Should()
            .Be(MemoryPredicateSeedVocabulary.Fingerprint)
            .And.MatchRegex("^[0-9a-f]{64}$");
        MemoryRelationSeedTable.Fingerprint.Should()
            .Be(MemoryRelationSeedTable.Fingerprint)
            .And.MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void TheExtractionVocabularyAndQueryLexiconHaveDistinctFingerprints()
    {
        // They are two different artifacts pointing in opposite directions: one decides what is
        // written, the other what is read. A single shared fingerprint would hide a change to either.
        MemoryPredicateSeedVocabulary.Fingerprint.Should()
            .NotBe(MemoryRelationSeedTable.Fingerprint);
    }

    [Fact]
    public void AddingASurfaceFormChangesTheQueryLexiconFingerprint()
    {
        // Surface forms never enter a prompt, but they change what a question resolves to and
        // therefore what is retrieved, so they belong in the fingerprint too.
        var before = MemoryVocabularyFingerprint.OfTable(
            new Dictionary<string, string[]> { ["bought"] = ["buy", "purchased"] });
        var after = MemoryVocabularyFingerprint.OfTable(
            new Dictionary<string, string[]> { ["bought"] = ["buy", "purchased", "acquired"] });

        after.Should().NotBe(before);
    }

    [Fact]
    public void MovingASurfaceFormBetweenRelationsChangesTheFingerprint()
    {
        // Same entry count, different meaning. A fingerprint over counts alone would miss this.
        var before = MemoryVocabularyFingerprint.OfTable(
            new Dictionary<string, string[]> { ["bought"] = ["got"], ["received"] = [] });
        var after = MemoryVocabularyFingerprint.OfTable(
            new Dictionary<string, string[]> { ["bought"] = [], ["received"] = ["got"] });

        after.Should().NotBe(before);
    }
}
