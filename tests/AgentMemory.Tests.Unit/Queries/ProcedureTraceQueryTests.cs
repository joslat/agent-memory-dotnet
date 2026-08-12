using AgentMemory.Neo4j.Queries;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// Promotion of a trace to a reusable procedure: the filter, and the exemption that makes it real.
/// </summary>
/// <remarks>
/// A trace and a procedure are the same record read two ways — an episode says what happened once,
/// a procedure says what to do next time, and they differ by <b>retrieval key</b>. Without the prune
/// exemption the distinction cannot survive: retention orders by age alone and would delete a
/// promoted procedure as soon as newer traces arrived.
/// </remarks>
public sealed class ProcedureTraceQueryTests
{
    [Fact]
    public void TheSearchIsUnchangedWhenNoProcedureFilterIsRequested()
    {
        // The TCK guard. /get_similar_traces takes every default, so a non-null default here would
        // change the Cypher it emits and break Gold 18/18 -- by filtering a corpus that holds no
        // promoted traces at all, i.e. to zero.
        var cypher = ReasoningQueries.SearchByTaskVector(false, true, true, 60);

        cypher.Should().NotContain("trace_kind");
    }

    [Fact]
    public void ProceduresOnlySelectsPromotedTraces()
    {
        var cypher = ReasoningQueries.SearchByTaskVector(false, true, true, 60, proceduresOnly: true);

        cypher.Should().Contain("coalesce(node.trace_kind, 'episode') = 'procedure'");
    }

    [Fact]
    public void ProceduresExcludedSelectsOrdinaryEpisodes()
    {
        var cypher = ReasoningQueries.SearchByTaskVector(false, true, true, 60, proceduresOnly: false);

        cypher.Should().Contain("coalesce(node.trace_kind, 'episode') <> 'procedure'");
    }

    [Fact]
    public void TheProcedureFilterIsNullSafeForTracesWrittenBeforeItExisted()
    {
        // A trace stored before trace_kind has the property NULL. A NULL-unsafe comparison would make
        // every legacy trace invisible to the episode filter -- silently emptying a corpus that is
        // entirely legacy, which is exactly what a pre-existing store is.
        foreach (var cypher in new[]
                 {
                     ReasoningQueries.SearchByTaskVector(false, false, true, 60, proceduresOnly: true),
                     ReasoningQueries.SearchByTaskVector(false, false, true, 60, proceduresOnly: false),
                 })
        {
            cypher.Should().Contain("coalesce(node.trace_kind, 'episode')");
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePruneExemptsPromotedProcedures(bool ownerIsShared)
    {
        // THE load-bearing clause. Retention orders by started_at with age as its only criterion and
        // fires on every trace creation once a cap is set, so without this a promoted procedure is
        // undone by recency and the capability does not exist.
        var cypher = ReasoningQueries.PruneSessionTraces(ownerIsShared);

        cypher.Should().Contain("coalesce(t.trace_kind, 'episode') <> 'procedure'");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePruneStillConfinesToASingleOwnerBucket(bool ownerIsShared)
    {
        // The exemption must not weaken the isolation guarantee it sits next to: a destructive write
        // keyed by a guessable session_id must never collapse to "all owners".
        var cypher = ReasoningQueries.PruneSessionTraces(ownerIsShared);

        if (ownerIsShared) cypher.Should().Contain("t.owner_id IS NULL");
        else cypher.Should().Contain("t.owner_id = $ownerId");
    }

    [Fact]
    public void ThePruneExemptionIsNullSafeSoLegacyTracesAreStillPruned()
    {
        // Written as "is NOT a procedure" rather than "is an episode": a NULL-unsafe form would exempt
        // every trace written before trace_kind existed, quietly turning a bounded store into an
        // unbounded one -- a retention cap that silently stops capping.
        var cypher = ReasoningQueries.PruneSessionTraces(false);

        cypher.Should().Contain("coalesce(t.trace_kind, 'episode')");
        cypher.Should().NotContain("t.trace_kind = 'episode'");
    }
}
