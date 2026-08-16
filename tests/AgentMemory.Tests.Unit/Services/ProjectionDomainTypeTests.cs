using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.2 step 1. The projection types exist, and their defaults are the off-state.
/// </summary>
/// <remarks>
/// Small, but each assertion pins a decision the rest of the feature depends on: that "no projection"
/// is representable and is the default, that "unset" is reference-distinguishable from "set to the
/// defaults" (which is how a request inherits the application-level value), and that no flag is on
/// out of the box.
/// </remarks>
public sealed class ProjectionDomainTypeTests
{
    [Fact]
    public void RecallOptionsDefaultsToTheAllOffProjectionSingleton()
    {
        // Reference equality, not value equality. The assembler distinguishes "the caller left this
        // alone" (inherit the app-level configuration) from "the caller explicitly asked for the
        // defaults" (use them), and only the singleton identity can carry that difference.
        RecallOptions.Default.Projection.Should().BeSameAs(MemoryProjectionOptions.Default);
        new RecallOptions().Projection.Should().BeSameAs(MemoryProjectionOptions.Default);
    }

    [Fact]
    public void EveryProjectionFeatureIsOffByDefault()
    {
        var options = MemoryProjectionOptions.Default;

        options.AnnotateMatchQuality.Should().BeFalse();
        options.ResolveSupersessions.Should().BeFalse();
        options.RenderConflicts.Should().BeFalse();
        options.AttachSourceQuotes.Should().BeFalse();
        options.GroundDates.Should().BeFalse();
        options.ChronologicalOrdering.Should().BeFalse();
    }

    [Fact]
    public void TheTraceNearMissThresholdIsTheMeasuredKneeAndNotTheSharedPrior()
    {
        // 0.92 is measured; 0.85 is a prior. Procedure retrieval behaves identically for every
        // threshold from 0.00 to 0.86 -- a dead zone in which it never abstains -- so sharing the
        // fact-side default would ship a threshold that provably does nothing.
        var options = MemoryProjectionOptions.Default;

        options.TraceNearMissThreshold.Should().Be(0.92);
        options.NearMissThreshold.Should().Be(0.85);
        options.TraceNearMissThreshold.Should().NotBe(options.NearMissThreshold);
    }

    [Fact]
    public void TheCapsAreSetSoTheTokenCostIsBoundedByConstruction()
    {
        var options = MemoryProjectionOptions.Default;

        options.MaxQuoteLength.Should().BePositive();
        options.MaxQuotesPerRecall.Should().BePositive();
        options.MaxSupersessionChain.Should().BePositive();
    }

    [Fact]
    public void AFreshMemoryContextCarriesNoProjection()
    {
        var context = new MemoryContext
        {
            SessionId = "s1",
            AssembledAtUtc = DateTimeOffset.UnixEpoch,
        };

        context.Projection.Should().BeNull(
            "null is the signal for every render surface to take its pre-existing path");
    }

    [Fact]
    public void AnEnabledButEmptyProjectionIsDistinguishableFromNoProjection()
    {
        // "Projection ran and found nothing" and "projection did not run" must not be the same value:
        // the first is a measurable outcome, the second is the off-state.
        var empty = new ProjectedContext();

        empty.Should().NotBeNull();
        empty.Annotations.Should().BeEmpty();
        empty.Blocks.Should().BeEmpty();
        empty.SectionOrder.Should().BeEmpty();
    }

    [Fact]
    public void AnnotationScoreIsNullableSoUnscoreableIsNotZero()
    {
        // An unscoreable provider and a terrible match are different facts. Collapsing them would let
        // an unscored section emit confident near-miss marks -- a fabricated abstention cue.
        new ProjectedItemAnnotation().Score.Should().BeNull();
        new ProjectedItemAnnotation().IsNearMiss.Should().BeFalse();
    }

    [Fact]
    public void TheReservedBlockKindsExistSoLaterFeaturesDoNotInventAFourthRenderPath()
    {
        Enum.GetValues<ProjectedBlockKind>().Should().Contain(
        [
            ProjectedBlockKind.NoDirectMatch,
            ProjectedBlockKind.ConflictingMemory,
            ProjectedBlockKind.WorkingMemoryProfile,
            ProjectedBlockKind.DueReminders,
            ProjectedBlockKind.DeltaSummary,
        ]);
    }
}
