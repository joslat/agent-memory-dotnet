using FluentAssertions;
using AgentMemory.LongMemEval;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// 30.9c wiring guard: the Wave-C ablation switches must reach the options that gate the features.
/// </summary>
/// <remarks>
/// <para>
/// Every Phase-30 Wave-C capability shipped off by default AND unreachable from the harness —
/// <c>LongMemEvalMemoryProfile</c> set three fields on <c>MemoryOptions</c> and none was a Phase-30
/// flag, and no CLI verb exposed one. So the features built to move these very numbers were the one
/// thing no run could exercise, and 30.6 sat "built, not measured" because it was
/// <b>unmeasurable</b>.
/// </para>
/// <para>
/// This is the class of defect this repository has hit repeatedly — fifteen recorded instances of
/// code that shipped, passed its own tests, and was reachable from nothing. The profile's own
/// <c>RescueShortOwnerResults</c> comment records the previous occurrence, one wave earlier. A guard
/// here is cheap; discovering it again after a paid measurement is not.
/// </para>
/// <para>
/// Verified end-to-end when the switch first ran: the ablation containers held <b>806 and 696</b>
/// <c>:Fact</c> nodes with <c>fact_kind='derived'</c>, against zero on the default path.
/// </para>
/// </remarks>
public sealed class PhaseThirtyFeaturesTests
{
    [Fact]
    public void AllOff_IsTheShippedDefault()
    {
        PhaseThirtyFeatures.AllOff.IsDefault.Should().BeTrue(
            "every sealed measurement so far was taken with all Wave-C features dark, and a run "
            + "that names no switch must keep taking that path");
        PhaseThirtyFeatures.AllOff.Extensions.Should().BeEmpty();
    }

    /// <summary>
    /// A flag without its schema extension is worse than a flag that does nothing: the DDL the
    /// feature writes through would be absent, so the write fails at the store and the feature reads
    /// as broken rather than dark.
    /// </summary>
    [Theory]
    [InlineData(true, false, "working-memory")]
    [InlineData(false, true, "arithmetic")]
    public void EnablingAFeatureCarriesItsSchemaExtension(
        bool workingMemory, bool arithmetic, string expected)
    {
        var features = new PhaseThirtyFeatures(workingMemory, arithmetic);

        features.Extensions.Should().ContainSingle().Which.Should().Be(expected);
        features.IsDefault.Should().BeFalse();
    }

    [Fact]
    public void EnablingBothCarriesBothExtensions()
    {
        var features = new PhaseThirtyFeatures(WorkingMemory: true, ArithmeticMemory: true);

        features.Extensions.Should().BeEquivalentTo(new[] { "working-memory", "arithmetic" });
        features.Describe().Should().Be("phase30:working-memory+arithmetic");
    }

    /// <summary>Provenance: a report must be able to say which configuration produced it.</summary>
    [Fact]
    public void DescribeNamesTheConfiguration()
    {
        PhaseThirtyFeatures.AllOff.Describe().Should().Be("phase30:none");
        new PhaseThirtyFeatures(ArithmeticMemory: true).Describe().Should().Be("phase30:arithmetic");
    }
}
