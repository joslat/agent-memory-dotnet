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
