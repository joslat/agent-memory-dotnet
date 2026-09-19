using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// E-1. The alias levers must reach the options, not merely parse.
/// </summary>
/// <remarks>
/// <para>
/// This is the eleventh reachable-but-never-fed lever this project has found, and the shape is always
/// the same: a capability is built, plumbed and tested, and nothing a measurement can set ever turns
/// it on — so the number published is the feature's off-state. The <c>aliases</c> field was the
/// purest example yet, being REQUIRED by the extraction schema the whole time.
/// </para>
/// <para>
/// The arm token is asserted as hard as the flags are. An ingestion lever produces a different store,
/// so a run that cannot be told apart from the default on disk is a run whose corpus cannot be
/// established afterwards.
/// </para>
/// </remarks>
public sealed class IdentityAliasWiringTests
{
    private static TypedMemEvalProgram.TypedMemEvalRunOptions Parse(params string[] extra) =>
        TypedMemEvalProgram.Parse(["--typedmemeval", "conjunction", .. extra]);

    [Theory]
    [InlineData("--capture-identity-aliases")]
    [InlineData("--expand-by-identity")]
    public void BothFlagsAreKnownOptions(string flag) =>
        TypedMemEvalProgram.KnownOptions.Should().Contain(flag);

    [Fact]
    public void TheCaptureFlagReachesTheOptionsRecord() =>
        Parse("--capture-identity-aliases").CaptureIdentityAliases.Should().BeTrue();

    [Fact]
    public void TheExpansionFlagReachesTheOptionsRecord() =>
        Parse("--expand-by-identity").ExpandFactsByIdentity.Should().BeTrue();

    /// <summary>
    /// The default arm is untouched, so every sealed measurement stays reproducible from it.
    /// </summary>
    [Fact]
    public void TheDefaultArmLeavesBothOff()
    {
        var off = Parse();

        off.CaptureIdentityAliases.Should().BeFalse();
        off.ExpandFactsByIdentity.Should().BeFalse();
        off.Arm.IsDefault.Should().BeTrue();
        off.Arm.FileToken().Should().Be("default");
    }

    /// <summary>
    /// The full intervention names all three levers, because it needs all three.
    /// </summary>
    /// <remarks>
    /// The hop walks <c>:ABOUT</c>, so without <c>--link-fact-entities</c> there is no edge to follow;
    /// and it only follows entities carrying an alias, so without <c>--capture-identity-aliases</c>
    /// there is no entity to follow through. Naming them separately on disk is what lets a later
    /// reader see which parts a given store actually had — the first three identity arms each had one
    /// part and each measured nothing.
    /// </remarks>
    [Fact]
    public void TheFullInterventionIsNameableOnDisk()
    {
        Parse("--link-fact-entities", "--capture-identity-aliases", "--expand-by-identity")
            .Arm.FileToken().Should().Be("entlink-alias-aliashop");

        Parse("--capture-identity-aliases").Arm.FileToken().Should().Be("alias");
        Parse("--expand-by-identity").Arm.FileToken().Should().Be("aliashop");
    }

    [Fact]
    public void TheDescriptionCarriesBothLevers()
    {
        var description = Parse("--capture-identity-aliases", "--expand-by-identity").Arm.Describe();

        description.Should().Contain("capture-identity-aliases=True");
        description.Should().Contain("expand-facts-by-identity=True");
    }

    /// <summary>
    /// An alias-capturing arm is a different CORPUS, so it can never read as the default one.
    /// </summary>
    [Fact]
    public void AnAliasCapturingArmIsNeverTheDefaultArm() =>
        Parse("--capture-identity-aliases").Arm.IsDefault.Should().BeFalse(
            "it changes what ingestion records, so its store is not the store the board was built on");
}

/// <summary>
/// The predicate-vocabulary lever, which no measured run could switch on.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is why bitemporal has never been anything but an off-state.</b> Write-time supersession
/// refuses any predicate outside the six the vocabulary declares single-valued
/// (<c>belongs to · costs · expires · lives in · weighs · works at</c>), and without the vocabulary
/// the extractor invents a predicate per sentence — measured at 700 facts under 421 distinct
/// predicates. The bitemporal store holds 6,839 facts whose commonest predicates are speech acts:
/// <c>came up with</c> ×1482, <c>said</c> ×1263. None can ever be recognised as replacing anything,
/// so <c>:SUPERSEDED_BY</c> was unwritable by construction.
/// </para>
/// <para>
/// The corpus does assert a genuinely single-valued state — "Alice Renwick is at Calderwick, as of
/// February", later corrected to Lowick — and extraction rendered it <c>is at</c> ×198, which is not
/// a declared surface form of <c>lives in</c>. <b>The fix is not to add it as one.</b> "Is at" is
/// ambiguous between a residence and standing in a doorway, and making it single-valued would let a
/// new one supersede the old, dropping a true fact from live recall for every consumer of this
/// library. Steering extraction is the designed mechanism; widening the lexicon to fit one corpus is
/// the tempting shortcut that damages everyone else.
/// </para>
/// </remarks>
public sealed class PredicateVocabularyWiringTests
{
    private static TypedMemEvalProgram.TypedMemEvalRunOptions Parse(params string[] extra) =>
        TypedMemEvalProgram.Parse(["--typedmemeval", "bitemporal", .. extra]);

    [Fact]
    public void TheFlagIsAKnownOption() =>
        TypedMemEvalProgram.KnownOptions.Should().Contain("--predicate-vocabulary");

    [Fact]
    public void TheFlagReachesTheOptionsRecord() =>
        Parse("--predicate-vocabulary").UsePredicateVocabulary.Should().BeTrue();

    /// <summary>Off by default, so every sealed measurement keeps its path.</summary>
    [Fact]
    public void TheDefaultArmLeavesItOff()
    {
        var off = Parse();

        off.UsePredicateVocabulary.Should().BeFalse();
        off.Arm.IsDefault.Should().BeTrue();
    }

    /// <summary>
    /// A vocabulary-steered run is a different CORPUS and its filename must say so.
    /// </summary>
    [Fact]
    public void AVocabularyArmIsItsOwnTokenBecauseItIsItsOwnCorpus()
    {
        Parse("--predicate-vocabulary").Arm.FileToken().Should().Be("vocab");
        Parse("--predicate-vocabulary", "--supersede-replaced-facts").Arm.FileToken()
            .Should().Be("supersede-vocab");
    }

    [Fact]
    public void TheDescriptionCarriesTheLever() =>
        Parse("--predicate-vocabulary").Arm.Describe()
            .Should().Contain("use-predicate-vocabulary=True");
}
