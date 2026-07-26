using AgentMemory.Abstractions.Options;
using AgentMemory.Cli;
using AgentMemory.Cli.Commands;
using AgentMemory.Cli.Perf;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class PerfAbTests
{
    [Fact]
    public void CliArgs_PerfAb_ExposesSubcommand()
    {
        var args = CliArgs.Parse(["perf", "ab", "--control", "default"]);

        args.Command.Should().Be("perf");
        args.Subcommand.Should().Be("ab");
        args.Get("control").Should().Be("default");
    }

    [Fact]
    public void Configuration_Default_UsesShippedRecallOptions()
    {
        var configuration = PerfConfiguration.Parse("default");

        configuration.Recall.Should().Be(RecallOptions.Default);
        configuration.CanonicalSpec.Should().Be("default");
    }

    [Fact]
    public void Configuration_MaxEntitiesOverride_ChangesOnlyMaxEntities()
    {
        var configuration = PerfConfiguration.Parse("Recall.MaxEntities=2");

        configuration.Recall.Should().Be(RecallOptions.Default with { MaxEntities = 2 });
        configuration.CanonicalSpec.Should().Be("Recall.MaxEntities=2");
        PerfFixture.ExpectedRecall(configuration.Recall).Total.Should().Be(35);
    }

    [Fact]
    public void Configuration_UnknownKey_IsRejected()
    {
        var act = () => PerfConfiguration.Parse("Recall.DoesNotExist=2");

        act.Should().Throw<ArgumentException>()
            .WithMessage("*DoesNotExist*");
    }

    [Fact]
    public void StatefulWriteScenario_IsNotEligibleForSharedDatabaseAb()
    {
        var scenario = PerfScenarios.All.Single(item => item.Id == "PERF-W-02");

        scenario.SupportsInterleavedAb.Should().BeFalse();
    }

    [Fact]
    public void AbDatasetIdentities_AreEquivalentButDisjoint()
    {
        var control = PerfFixture.ForVariant("control");
        var candidate = PerfFixture.ForVariant("candidate");

        control.OwnerId.Should().NotBe(candidate.OwnerId);
        control.SessionId.Should().NotBe(candidate.SessionId);
        control.ConversationId.Should().NotBe(candidate.ConversationId);
        control.TopicToken.Length.Should().Be(candidate.TopicToken.Length);
        PerfFixture.ProbeQueryFor(control).Length.Should().Be(PerfFixture.ProbeQueryFor(candidate).Length);

        control.IdPrefix.Should().NotBe(candidate.IdPrefix);
    }

    [Fact]
    public void DatasetCrossover_BalancesBothConfigurationsAcrossBothCopies()
    {
        PerfAbCommand.DatasetVariantFor("control", 0).Should().Be("control");
        PerfAbCommand.DatasetVariantFor("candidate", 0).Should().Be("candidate");
        PerfAbCommand.DatasetVariantFor("control", 1).Should().Be("candidate");
        PerfAbCommand.DatasetVariantFor("candidate", 1).Should().Be("control");
    }

    [Fact]
    public void PairedBootstrap_IdenticalSamples_ReportNoSignificantDifference()
    {
        var result = PairedRatioBootstrap.Analyze(
            [10, 11, 9, 12, 10],
            [10, 11, 9, 12, 10],
            resamples: 2_000,
            seed: 21);

        result.Estimate.Should().Be(1);
        result.Lower95.Should().Be(1);
        result.Upper95.Should().Be(1);
        result.Verdict.Should().Be(TimingVerdict.NoSignificantDifference);
    }

    [Fact]
    public void PairedBootstrap_UniformHalving_DeclaresImprovement()
    {
        var result = PairedRatioBootstrap.Analyze(
            [10, 20, 30, 40, 50],
            [5, 10, 15, 20, 25],
            resamples: 2_000,
            seed: 21);

        result.Estimate.Should().BeApproximately(0.5, 1e-12);
        result.Upper95.Should().BeLessThan(1);
        result.Verdict.Should().Be(TimingVerdict.Improvement);
    }

    [Fact]
    public void PairedBootstrap_UniformDoubling_DeclaresRegression()
    {
        var result = PairedRatioBootstrap.Analyze(
            [10, 20, 30, 40, 50],
            [20, 40, 60, 80, 100],
            resamples: 2_000,
            seed: 21);

        result.Estimate.Should().BeApproximately(2, 1e-12);
        result.Lower95.Should().BeGreaterThan(1);
        result.Verdict.Should().Be(TimingVerdict.Regression);
    }

    [Fact]
    public void CounterbalancedBootstrap_FirstSecondEffect_ReportsNoSignificantDifference()
    {
        // The second runner is 10% faster: candidate benefits in AB, control benefits in BA.
        // Each six-pair cluster has three of each order, so execution position cancels exactly.
        var result = PairedRatioBootstrap.AnalyzeCounterbalanced(
            [100, 90, 100, 90, 100, 90, 100, 90, 100, 90, 100, 90],
            [90, 100, 90, 100, 90, 100, 90, 100, 90, 100, 90, 100],
            resamples: 2_000,
            seed: 21);

        result.Pairs.Should().Be(2);
        result.Estimate.Should().Be(1);
        result.Lower95.Should().Be(1);
        result.Upper95.Should().Be(1);
        result.Verdict.Should().Be(TimingVerdict.NoSignificantDifference);
    }

    [Fact]
    public void CounterbalancedBootstrap_RequiresCompleteAbBaBlocks()
    {
        var act = () => PairedRatioBootstrap.AnalyzeCounterbalanced(
            [100, 90, 100, 90, 100, 90, 100, 90, 100, 90],
            [90, 100, 90, 100, 90, 100, 90, 100, 90, 100]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*multiple of six*");
    }

    [Fact]
    public void PairedBootstrap_RejectsNonPositiveTimings()
    {
        var act = () => PairedRatioBootstrap.Analyze([10, 0], [9, 1]);

        act.Should().Throw<ArgumentException>()
            .WithMessage("*positive*");
    }
}
