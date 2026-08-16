using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Queries;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.8 step 8. The probe reaches a real implementation, and asks the question it claims to ask.
/// </summary>
/// <remarks>
/// <para>
/// Another feature built on empty-returning default interface methods, and the same trap: a shipped
/// implementation that forgot to override one is indistinguishable from a graph where nothing had been
/// forgotten. Permanent silence, no error, no failing test.
/// </para>
/// <para>
/// The Cypher assertions below are about a distinction that is invisible at runtime until it is wrong.
/// A superseded fact is <i>also</i> invalidated; reporting one as forgotten would be a lie in the most
/// damaging direction, because the system did not forget it — it replaced it, and the replacement is
/// live and should be answering the question.
/// </para>
/// </remarks>
public sealed class LegibleForgettingReachabilityTests
{
    private static bool DeclaresOwnImplementation(Type type, Type contract, string method)
    {
        var map = type.GetInterfaceMap(contract);
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == method);
        index.Should().BeGreaterThanOrEqualTo(0, "{0} must declare {1}", contract.Name, method);
        return map.TargetMethods[index].DeclaringType != contract;
    }

    [Fact]
    public void TheNeo4jRepositoryImplementsTheProbe()
    {
        DeclaresOwnImplementation(
            typeof(AgentMemory.Neo4j.Repositories.Neo4jFactRepository),
            typeof(IFactRepository),
            nameof(IFactRepository.SearchDecayedFactsAsync))
            .Should().BeTrue();
    }

    [Fact]
    public void TheCoreServiceForwardsTheProbe()
    {
        var service = typeof(AgentMemory.Core.ServiceCollectionExtensions).Assembly
            .GetType("AgentMemory.Core.Services.LongTermMemoryService")!;

        DeclaresOwnImplementation(
            service, typeof(ILongTermMemoryService),
            nameof(ILongTermMemoryService.SearchDecayedFactsAsync))
            .Should().BeTrue();
    }

    // ── the probe asks for decayed, not merely invalidated ────────────

    [Fact]
    public void TheProbeRequiresTheDecayReasonAndNotJustInvalidation()
    {
        // THE distinction. Without the reason clause a superseded fact would be reported as forgotten,
        // which is wrong in the direction that matters: its replacement is live and should be answering.
        var cypher = FactQueries.SearchDecayedByVector(false, false, 10);

        cypher.Should().Contain("node.invalidated_at IS NOT NULL");
        cypher.Should().Contain("node.invalidated_reason = 'decay'");
    }

    [Fact]
    public void TheProbeDoesNotFilterForLiveFacts()
    {
        // The inverted clause. Present would mean the probe searches live memory and finds nothing
        // forgotten, ever -- a feature that ships, runs, and is silent by construction.
        FactQueries.SearchDecayedByVector(false, false, 10)
            .Should().NotContain("node.invalidated_at IS NULL");
    }

    [Fact]
    public void TheProbeAppliesTheSameSimilarityFloorALiveSearchWould()
    {
        // A tombstone clearing a looser bar is a confident claim about having forgotten something on an
        // unrelated topic -- worse than silence, because it invites the user to re-supply information
        // they never gave.
        FactQueries.SearchDecayedByVector(false, false, 10).Should().Contain("score >= $minScore");
    }

    [Fact]
    public void TheProbeIsOwnerScopedTheSameWayLiveSearchIs()
    {
        FactQueries.SearchDecayedByVector(hasOwnerFilter: true, includeShared: false, 10)
            .Should().Contain("node.owner_id = $ownerId");
        FactQueries.SearchDecayedByVector(hasOwnerFilter: true, includeShared: true, 10)
            .Should().Contain("node.owner_id IS NULL");
        FactQueries.SearchDecayedByVector(hasOwnerFilter: false, includeShared: false, 10)
            .Should().NotContain("$ownerId");
    }

    [Fact]
    public void TheProbeReusesTheExistingFactVectorIndex()
    {
        // Zero parity cost rests on this: soft-invalidation keeps the embedding, so these nodes are
        // already in the index and every live query simply filters them out afterwards.
        FactQueries.SearchDecayedByVector(false, false, 10).Should().Contain("fact_embedding_idx");
    }

    // ── the write side stamps the reason ──────────────────────────────

    [Fact]
    public void TheNonDestructivePruneStampsWhy()
    {
        DecayQueries.PruneFacts(hasOwnerFilter: false, nonDestructive: true)
            .Should().Contain("invalidated_reason = coalesce(f.invalidated_reason, 'decay')");
    }

    [Fact]
    public void TheDestructivePruneIsUntouched()
    {
        // A deleted node has no reason to carry, and widening the destructive branch would be a change
        // to a GDPR path made for the convenience of a diagnostic line.
        var destructive = DecayQueries.PruneFacts(hasOwnerFilter: false, nonDestructive: false);

        destructive.Should().Contain("DETACH DELETE");
        destructive.Should().NotContain("invalidated_reason");
    }

    [Fact]
    public void TheReasonStampIsIdempotent()
    {
        // coalesce preserves a reason already stamped, so a re-run cannot overwrite one -- alongside
        // the existing `invalidated_at IS NULL` guard that stops a re-run re-counting the same nodes.
        var cypher = DecayQueries.PruneFacts(hasOwnerFilter: false, nonDestructive: true);

        cypher.Should().Contain("coalesce(f.invalidated_reason, 'decay')");
        cypher.Should().Contain("f.invalidated_at IS NULL");
    }

    [Fact]
    public void SupersessionDoesNotStampADecayReason()
    {
        // Superseded is not decayed, and the null reason IS the partition. If supersession stamped one,
        // every replaced fact would become reportable as forgotten.
        FactQueries.Supersede(hasOwnerFilter: false).Should().NotContain("invalidated_reason");
        FactQueries.Invalidate(hasOwnerFilter: false).Should().NotContain("invalidated_reason");
    }
}
