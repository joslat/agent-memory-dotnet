using System.Reflection;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Per-question graph verification must compare what the seal actually recorded (22.4 blocker).
/// </summary>
/// <remarks>
/// <para>
/// 6.5 made <c>ReasoningTraces</c> and <c>Procedures</c> nullable on purpose, so a legacy manifest
/// reads as <i>not measured</i> rather than as measured-and-zero. Verification then compared with
/// record <c>Equals</c>, which compares every field — so a sealed snapshot holding nulls could never
/// equal a freshly probed one holding counts.
/// </para>
/// <para>
/// The effect was total and silent: every question in a pre-6.5 corpus failed as
/// <c>prepared-graph-mismatch</c>, retrieved nothing, and produced an unusable run that still cost
/// ~200 provider calls. The graph was fine; the comparison asked about a field the manifest was never
/// able to record.
/// </para>
/// </remarks>
public sealed class GraphSnapshotSealComparisonTests
{
    private static object Snapshot(int? traces = null, int? procedures = null, int facts = 10)
    {
        var type = typeof(LongMemEvalOracleComparison).Assembly
            .GetType("AgentMemory.LongMemEval.LongMemEvalGraphSnapshot")!;
        return Activator.CreateInstance(
            type, 5, facts, 3, 2, 2, 18, 18, 40, 12, traces, procedures)!;
    }

    private static bool Matches(object probed, object sealedSnapshot) =>
        (bool)probed.GetType()
            .GetMethod("MatchesSealed", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(probed, [sealedSnapshot])!;

    [Fact]
    public void ASealWithoutTraceCountersMatchesAProbeThatHasThem()
    {
        // THE regression. A corpus sealed before 6.5 records nulls; today's probe counts traces and
        // procedures. Under record equality this was false for every question in every such corpus.
        var sealedBefore65 = Snapshot(traces: null, procedures: null);
        var probedToday = Snapshot(traces: 7, procedures: 2);

        Matches(probedToday, sealedBefore65).Should().BeTrue(
            "a counter the manifest could not record is not evidence of a mismatch");
    }

    [Fact]
    public void ASealThatRecordedTraceCountersStillComparesThem()
    {
        // The exemption must be scoped to what was genuinely unrecordable. Once a seal carries the
        // counters, a disagreement is a real one and must fail.
        var sealedWithCounters = Snapshot(traces: 7, procedures: 2);

        Matches(Snapshot(traces: 7, procedures: 2), sealedWithCounters).Should().BeTrue();
        Matches(Snapshot(traces: 9, procedures: 2), sealedWithCounters).Should().BeFalse();
        Matches(Snapshot(traces: 7, procedures: 5), sealedWithCounters).Should().BeFalse();
    }

    [Fact]
    public void AGenuineGraphDifferenceStillFails()
    {
        // The check exists to catch a graph that is not the one the manifest describes. Relaxing the
        // two late-added counters must not blunt that -- otherwise a corpus could be silently swapped.
        var sealedSnapshot = Snapshot(facts: 10);

        Matches(Snapshot(facts: 11), sealedSnapshot).Should().BeFalse(
            "a different fact count is a different graph");
    }

    [Fact]
    public void AProbeMissingCountersTheSealHasIsAMismatch()
    {
        // Asymmetric on purpose. A null on the SEALED side means "not recorded"; a null on the PROBED
        // side means the probe failed to count something it should have, which is a real fault and
        // must not be waved through.
        var sealedWithCounters = Snapshot(traces: 7, procedures: 2);

        Matches(Snapshot(traces: null, procedures: null), sealedWithCounters).Should().BeFalse();
    }
}
