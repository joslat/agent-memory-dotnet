using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// 30.7 step 8. The production implementations override the firing members rather than inheriting the
/// empty default.
/// </summary>
/// <remarks>
/// <para>
/// Firing is built out of default interface methods that return <b>empty</b>. That is the right default
/// — a store with no valid-time query simply does not fire — and it is also the most dangerous shape a
/// feature can have, because a shipped implementation that forgot to override one is indistinguishable
/// from a graph where nothing happened to be due. No error, no log, no failing test: just permanent
/// silence.
/// </para>
/// <para>
/// So the override is asserted structurally, on the shipped types, in both layers.
/// </para>
/// </remarks>
public sealed class ProspectiveFiringReachabilityTests
{
    private static bool DeclaresOwnImplementation(Type type, Type contract, string method)
    {
        var map = type.GetInterfaceMap(contract);
        var index = Array.FindIndex(map.InterfaceMethods, m => m.Name == method);
        index.Should().BeGreaterThanOrEqualTo(0, "{0} must declare {1}", contract.Name, method);

        // A default interface member maps back to the interface; a real override maps to the type.
        return map.TargetMethods[index].DeclaringType != contract;
    }

    [Fact]
    public void TheNeo4jRepositoryImplementsFiringRatherThanInheritingSilence()
    {
        DeclaresOwnImplementation(
            typeof(AgentMemory.Neo4j.Repositories.Neo4jFactRepository),
            typeof(IFactRepository),
            nameof(IFactRepository.GetDueFactsAsync))
            .Should().BeTrue();
    }

    [Fact]
    public void TheCoreServiceForwardsFiringRatherThanInheritingSilence()
    {
        // The seam that has failed before in this exact shape: the repository took the argument and the
        // service passed null, so every shipped recall path asked the wrong question and nothing failed.
        var service = typeof(AgentMemory.Core.ServiceCollectionExtensions).Assembly
            .GetType("AgentMemory.Core.Services.LongTermMemoryService")!;

        DeclaresOwnImplementation(
            service, typeof(ILongTermMemoryService), nameof(ILongTermMemoryService.GetDueFactsAsync))
            .Should().BeTrue();
    }

    [Fact]
    public void TheFiringQueriesCarryNoSimilarityMachinery()
    {
        // The absence IS the specification. A "$embedding" or "$minScore" appearing here would mean
        // someone had scoped reminders by similarity to the current query -- which is exactly the
        // failure firing exists to fix, since a reminder is off-topic by definition.
        foreach (var hasOwner in new[] { true, false })
        {
            foreach (var cypher in new[]
            {
                AgentMemory.Neo4j.Queries.FactQueries.GetDueFacts(hasOwner, includeShared: false),
                AgentMemory.Neo4j.Queries.FactQueries.GetExpiringFacts(hasOwner, includeShared: false),
            })
            {
                cypher.Should().NotContain("$embedding");
                cypher.Should().NotContain("$minScore");
                cypher.Should().NotContain("vector.similarity");
            }
        }
    }

    [Fact]
    public void TheDueQueryReadsTheValidTimeClockAndNotTheTransactionClock()
    {
        // "I learned last week that the renewal is today" must fire TODAY. created_at would fire it
        // last week -- a reminder delivered before it was relevant, which is how reminders get ignored.
        var cypher = AgentMemory.Neo4j.Queries.FactQueries.GetDueFacts(false, false);

        cypher.Should().Contain("f.valid_from > datetime($since)");
        cypher.Should().NotContain("created_at");
    }

    [Fact]
    public void TheExpiringQueryExcludesWhatHasAlreadyExpired()
    {
        // Already-expired belongs to delta recall's expired-validity bucket. Reporting it as
        // "expiring" is a tense error the reader acts on.
        AgentMemory.Neo4j.Queries.FactQueries.GetExpiringFacts(false, false)
            .Should().Contain("f.valid_until > datetime($now)");
    }

    [Fact]
    public void NeitherFiringQueryReturnsAnInvalidatedFact()
    {
        AgentMemory.Neo4j.Queries.FactQueries.GetDueFacts(false, false)
            .Should().Contain("f.invalidated_at IS NULL");
        AgentMemory.Neo4j.Queries.FactQueries.GetExpiringFacts(false, false)
            .Should().Contain("f.invalidated_at IS NULL");
    }

    [Fact]
    public void TheOwnerClauseIsAppliedWhenScopedAndAbsentWhenNot()
    {
        AgentMemory.Neo4j.Queries.FactQueries.GetDueFacts(hasOwnerFilter: true, includeShared: false)
            .Should().Contain("f.owner_id = $ownerId");
        AgentMemory.Neo4j.Queries.FactQueries.GetDueFacts(hasOwnerFilter: false, includeShared: false)
            .Should().NotContain("$ownerId");
    }

    [Fact]
    public void ParityLeavesFiringOff()
    {
        // Parity means "ranks exactly like upstream", and upstream has no firing. Firing defaults off
        // and MemoryProfile.Parity is the default profile, so this holds by construction -- asserted
        // rather than assumed, because it is the kind of property a later default flip would break
        // without anyone connecting the two.
        var ranking = new MemoryRankingOptions();

        ranking.Profile.Should().Be(MemoryProfile.Parity);
        new RecallOptions().ProspectiveFiring.Should().BeFalse();
    }
}
