using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// D7 — the supersession writers (`{Fact,Preference}Queries.Supersede`) close a loser non-destructively
/// (stamp the transaction clock idempotently, never delete) and link <c>(loser)-[:SUPERSEDED_BY]-&gt;(winner)</c>,
/// and the non-destructive duplicate-preference collapse soft-invalidates rather than deleting.
/// All are owner-scoped (R1): the owner predicate on BOTH endpoints appears only when a scope is supplied.
/// </summary>
public sealed class SupersedeQueryTests
{
    public static IEnumerable<object[]> AllSupersede()
    {
        yield return new object[] { "Fact", (Func<bool, string>)FactQueries.Supersede };
        yield return new object[] { "Preference", (Func<bool, string>)PreferenceQueries.Supersede };
    }

    [Theory]
    [MemberData(nameof(AllSupersede))]
    public void Supersede_ClosesLoserIdempotently_LinksSupersededBy_AndNeverDeletes(string label, Func<bool, string> build)
    {
        var cypher = build(/*hasOwnerFilter*/ false);

        cypher.Should().Contain("loser.invalidated_at = coalesce(",
            $"{label} supersession must close the loser on the transaction clock, idempotently");
        cypher.Should().Contain("MERGE (loser)-[:SUPERSEDED_BY]->(winner)",
            $"{label} supersession must link loser to winner non-destructively");
        cypher.Should().Contain("datetime($now)");
        cypher.Should().NotContain("DELETE", $"{label} supersession must never delete");
        cypher.Should().Contain("count(loser) > 0 AS superseded");
    }

    [Fact]
    public void Supersede_Fact_AlsoClosesValidTimeWindow()
    {
        // Facts carry a real-world validity window, so supersession also closes valid_until (valid clock).
        FactQueries.Supersede(false).Should().Contain("loser.valid_until    = coalesce(loser.valid_until, datetime($now))");
    }

    [Fact]
    public void Supersede_Preference_DoesNotTouchValidUntil()
    {
        // Preferences have no valid-time window in this schema — only the transaction clock applies.
        PreferenceQueries.Supersede(false).Should().NotContain("valid_until");
    }

    [Theory]
    [MemberData(nameof(AllSupersede))]
    public void Supersede_Unscoped_HasNoScopingPredicate_ButStillGuardsSameOwner(string label, Func<bool, string> build)
    {
        var cypher = build(/*hasOwnerFilter*/ false);
        cypher.Should().NotContain("owner_id = $ownerId",
            $"{label} unscoped supersession must not bind a specific owner");
        // …but it must STILL refuse to link across owners, even on the unscoped (admin) path.
        cypher.Should().Contain("coalesce(loser.owner_id, '*') = coalesce(winner.owner_id, '*')",
            $"{label} supersession must never create a cross-owner :SUPERSEDED_BY edge");
    }

    [Theory]
    [MemberData(nameof(AllSupersede))]
    public void Supersede_Scoped_ScopesBothEndpointsToOwner(string label, Func<bool, string> build)
    {
        var cypher = build(/*hasOwnerFilter*/ true);
        cypher.Should().Contain("loser.owner_id = $ownerId",
            $"{label} scoped supersession must confine the loser to the owner");
        cypher.Should().Contain("winner.owner_id = $ownerId",
            $"{label} scoped supersession must confine the winner to the owner (no cross-owner link)");
    }

    [Theory]
    [MemberData(nameof(AllSupersede))]
    public void Supersede_AlwaysGuardsSameOwner(string label, Func<bool, string> build)
    {
        // The same-owner guard is unconditional — present in BOTH the scoped and unscoped query shapes.
        build(true).Should().Contain("coalesce(loser.owner_id, '*') = coalesce(winner.owner_id, '*')");
        build(false).Should().Contain("coalesce(loser.owner_id, '*') = coalesce(winner.owner_id, '*')");
    }

    [Theory]
    [MemberData(nameof(AllSupersede))]
    public void Supersede_RejectsSelfSupersede(string label, Func<bool, string> build)
    {
        // `loser <> winner` makes a same-id supersede match zero rows (returns false) rather than
        // invalidating a live node and creating a :SUPERSEDED_BY self-loop.
        build(true).Should().Contain("loser <> winner", $"{label} scoped supersede must reject self-supersede");
        build(false).Should().Contain("loser <> winner", $"{label} unscoped supersede must reject self-supersede");
    }

    [Fact]
    public void RemoveDuplicatePreferences_IsNonDestructive_AndIdempotent()
    {
        var cypher = ConsolidationQueries.RemoveDuplicatePreferences;

        cypher.Should().NotContain("DETACH DELETE", "duplicate collapse must be non-destructive (D7)");
        cypher.Should().Contain("SET dup.invalidated_at = coalesce(dup.invalidated_at, datetime())",
            "older duplicates are soft-invalidated, not deleted");
        cypher.Should().Contain("MERGE (dup)-[:SUPERSEDED_BY]->(keep)",
            "each duplicate is linked to the kept (newest) preference");
        cypher.Should().Contain("WHERE p.invalidated_at IS NULL",
            "only live preferences are grouped, so a re-run is idempotent");
    }

    [Fact]
    public void CountDuplicatePreferences_IgnoresAlreadyInvalidated()
    {
        ConsolidationQueries.CountDuplicatePreferences.Should().Contain("WHERE p.invalidated_at IS NULL",
            "the dry-run count must match what a non-destructive collapse would close");
    }
}
