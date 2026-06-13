using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// D5 — the soft-invalidate writers (`{Fact,Entity,Preference}Queries.Invalidate`) stamp
/// <c>invalidated_at</c> idempotently, are owner-scoped (R1: the owner predicate appears only when a
/// scope is supplied), and never delete.
/// </summary>
public sealed class InvalidateQueryTests
{
    public static IEnumerable<object[]> AllInvalidate()
    {
        yield return new object[] { "Fact", (Func<bool, string>)FactQueries.Invalidate };
        yield return new object[] { "Entity", (Func<bool, string>)EntityQueries.Invalidate };
        yield return new object[] { "Preference", (Func<bool, string>)PreferenceQueries.Invalidate };
    }

    [Theory]
    [MemberData(nameof(AllInvalidate))]
    public void Invalidate_StampsInvalidatedAt_Idempotently_AndNeverDeletes(string label, Func<bool, string> build)
    {
        var cypher = build(/*hasOwnerFilter*/ false);

        cypher.Should().Contain("invalidated_at = coalesce(",
            $"{label} invalidation must preserve the first invalidation time (idempotent)");
        cypher.Should().Contain("datetime($now)");
        cypher.Should().NotContain("DELETE", $"{label} invalidation must be non-destructive");
    }

    [Theory]
    [MemberData(nameof(AllInvalidate))]
    public void Invalidate_Unscoped_HasNoOwnerPredicate(string label, Func<bool, string> build)
    {
        build(/*hasOwnerFilter*/ false).Should().NotContain("owner_id",
            $"{label} unscoped invalidation must not filter by owner");
    }

    [Theory]
    [MemberData(nameof(AllInvalidate))]
    public void Invalidate_Scoped_AddsOwnerPredicate(string label, Func<bool, string> build)
    {
        build(/*hasOwnerFilter*/ true).Should().Contain("owner_id = $ownerId",
            $"{label} scoped invalidation must only touch the owner's own node");
    }
}
