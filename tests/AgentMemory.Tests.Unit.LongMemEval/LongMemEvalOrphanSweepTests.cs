using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Retention keeps a cold build on disk so it can be reused, and a killed run never gets to clean up
/// after itself. Measured before this was written: 25 orphaned volumes holding ~17.2 GB. The removal
/// decision is kept pure so it can be tested without a Docker daemon.
/// </summary>
public sealed class LongMemEvalOrphanSweepTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 8, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void OldUnreferencedClonesAreRemoved()
    {
        // Clones are regenerable from a base in seconds; they are the bulk of the leak.
        var decision = Select(
            Volume("am-lme-run-a-structured-1111", hoursAgo: 6),
            Volume("am-lme-run-a-hybrid-1111", hoursAgo: 6));

        decision.Removable.Should().BeEquivalentTo(
            "am-lme-run-a-structured-1111", "am-lme-run-a-hybrid-1111");
    }

    [Fact]
    public void TheVolumeNamedForReuseIsNeverRemoved()
    {
        var decision = Select(
            protectedVolumeName: "am-lme-run-a-base-1111",
            Volume("am-lme-run-a-base-1111", hoursAgo: 6),
            Volume("am-lme-run-b-base-2222", hoursAgo: 7));

        decision.Removable.Should().NotContain("am-lme-run-a-base-1111");
        decision.Skipped.Should().ContainSingle(skip =>
            skip.Name == "am-lme-run-a-base-1111" && skip.Reason.Contains("reuse"));
    }

    [Fact]
    public void CloneTargetsOfTheReusedVolumeAreNeverRemoved()
    {
        // AdoptAsync names its clone targets after the adopted base, so a prefix match protects the
        // in-flight clones of the very run performing the sweep.
        var decision = Select(
            protectedVolumeName: "am-lme-run-a-base-1111",
            Volume("am-lme-run-a-base-1111-reuse-structured-abcd", hoursAgo: 9));

        decision.Removable.Should().BeEmpty();
    }

    [Fact]
    public void VolumesYoungerThanTheMinimumAgeAreNeverRemoved()
    {
        // The load-bearing guard: a concurrently running evaluation creates its clone volumes long
        // before it mounts them, so a fresh unreferenced volume may belong to a live run.
        var decision = Select(
            Volume("am-lme-run-a-structured-1111", hoursAgo: 0.25),
            Volume("am-lme-run-a-base-1111", hoursAgo: 0.25),
            Volume("am-lme-run-old-hybrid-9999", hoursAgo: 40));

        decision.Removable.Should().ContainSingle()
            .Which.Should().Be("am-lme-run-old-hybrid-9999");
        decision.Skipped.Should().Contain(skip =>
            skip.Name == "am-lme-run-a-structured-1111" && skip.Reason.Contains("age"));
    }

    [Fact]
    public void TheNewestBaseIsKeptBecauseItRepresentsAPaidColdBuild()
    {
        // A base is 121 provider calls and ~22 minutes. Older ones are garbage; the newest is the
        // one a retrieval-only experiment would want to adopt.
        var decision = Select(
            Volume("am-lme-run-old-base-1111", hoursAgo: 9),
            Volume("am-lme-run-new-base-2222", hoursAgo: 6),
            Volume("am-lme-run-new-structured-2222", hoursAgo: 6));

        decision.Removable.Should().BeEquivalentTo(
            "am-lme-run-old-base-1111", "am-lme-run-new-structured-2222");
        decision.Skipped.Should().Contain(skip =>
            skip.Name == "am-lme-run-new-base-2222" && skip.Reason.Contains("newest"));
    }

    [Fact]
    public void VolumesOutsideTheLongMemEvalNamespaceAreNeverConsidered()
    {
        // The sweep runs on a developer machine that has unrelated Docker volumes on it.
        var decision = Select(
            Volume("postgres-data", hoursAgo: 500),
            Volume("277e3702a0f44f437072b60fd4d26f1d15c51f96fb12e17ddd6cc16711cc677d", hoursAgo: 500));

        decision.Removable.Should().BeEmpty();
        decision.Skipped.Should().BeEmpty();
    }

    private static LongMemEvalOrphanSweepDecision Select(
        params LongMemEvalVolumeCandidate[] candidates) =>
        LongMemEvalOrphanSweep.Select(candidates, protectedVolumeName: null, Now);

    private static LongMemEvalOrphanSweepDecision Select(
        string? protectedVolumeName,
        params LongMemEvalVolumeCandidate[] candidates) =>
        LongMemEvalOrphanSweep.Select(candidates, protectedVolumeName, Now);

    private static LongMemEvalVolumeCandidate Volume(string name, double hoursAgo) =>
        new(name, Now.AddHours(-hoursAgo));
}
