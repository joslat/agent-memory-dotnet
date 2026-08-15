using AgentMemory.Neo4j.Queries;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Queries;

/// <summary>
/// 30.6 guards G1 and G2, from the TCK audit. Both are binding, and both are about what the Cypher
/// must <b>not</b> do.
/// </summary>
/// <remarks>
/// <para>
/// <b>G1 — the cascade must be cardinality-safe.</b> It is spliced into <c>Supersede</c> and
/// <c>Invalidate</c>, whose callers read a row count to decide whether the operation worked. A fact
/// with N derived dependants would multiply the outer row N times without a <c>WITH DISTINCT</c>, and
/// the caller would report success N times for one supersession — or, on a store with no derived facts
/// at all, the cascade must produce exactly the rows it did before the feature existed.
/// </para>
/// <para>
/// <b>G2 — the fact upsert must not be able to merge into a derived node.</b> A user restating a number
/// could otherwise land on an aggregate, overwriting its value while leaving its <c>DERIVED_FROM</c>
/// edges and derivation string in place: a fact wearing provenance for arithmetic that never produced
/// it. MERGE cannot carry a <c>WHERE</c>, so this is enforced by <b>omission</b> — a derived fact simply
/// has no merge-key quadruple, which makes the collision unreachable rather than unlikely.
/// </para>
/// <para>
/// These are text assertions on Cypher, which is a weak instrument in general — but the properties they
/// hold are structural, live in one statement each, and would otherwise be checked only by a live-graph
/// test that has to be looking for exactly the right shape.
/// </para>
/// </remarks>
public sealed class DerivedFactCypherGuardTests
{
    // ── G1: cardinality safety ────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheCascadeIsSplicedIntoInvalidate(bool hasOwnerFilter)
    {
        FactQueries.Invalidate(hasOwnerFilter).Should().Contain("DERIVED_FROM",
            "an aggregate over a retracted fact is stale the instant the retraction lands");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheCascadeIsSplicedIntoSupersede(bool hasOwnerFilter)
    {
        FactQueries.Supersede(hasOwnerFilter).Should().Contain("DERIVED_FROM");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheCascadeRunsInTheSameStatementAsTheRetraction(bool hasOwnerFilter)
    {
        // Same statement or nothing. An eventually-consistent sweep leaves a window in which a derived
        // 750 whose input 800 was just superseded is still retrievable, embedded, and carrying inline
        // provenance that makes it look verified.
        var cypher = FactQueries.Supersede(hasOwnerFilter);

        cypher.IndexOf("SUPERSEDED_BY", StringComparison.Ordinal)
            .Should().BeLessThan(cypher.IndexOf("DERIVED_FROM", StringComparison.Ordinal));
        cypher.Should().Contain("RETURN count(loser) > 0 AS superseded",
            "the cascade must precede the return, inside one statement");
    }

    [Fact]
    public void TheCascadeUsesOptionalMatchSoAStoreWithNoDerivedFactsIsUnaffected()
    {
        var cascade = DerivedFactQueries.CascadeInvalidateDerived("f");

        cascade.Should().Contain("OPTIONAL MATCH",
            "a plain MATCH would drop the row entirely when no aggregate exists, turning every "
            + "supersession on an ordinary store into a reported failure");
    }

    [Fact]
    public void TheCascadeCollapsesRowsOnBothSides()
    {
        // The row-multiplication guard, stated twice because it has to hold going in AND coming out: a
        // fact with N dependants would otherwise leave N copies of the outer row for the RETURN to
        // count.
        var cascade = DerivedFactQueries.CascadeInvalidateDerived("loser");

        cascade.TrimStart().Should().StartWith("WITH DISTINCT loser");
        cascade.TrimEnd().Should().EndWith("WITH DISTINCT loser");
    }

    [Fact]
    public void TheCascadeIsIdempotentSoARepeatedRetractionDoesNotRestampIt()
    {
        // coalesce preserves the FIRST invalidation instant. Overwriting it would move a derived fact's
        // death forward in time on every subsequent supersession of any of its inputs, and as-of recall
        // reads that timestamp.
        DerivedFactQueries.CascadeInvalidateDerived("f")
            .Should().Contain("coalesce(derived.invalidated_at, datetime($now))");
    }

    [Fact]
    public void TheCascadeOnlyEverInvalidatesNeverDeletes()
    {
        var cascade = DerivedFactQueries.CascadeInvalidateDerived("f");

        cascade.Should().NotContain("DELETE");
        cascade.Should().NotContain("REMOVE");
    }

    // ── G2: the upsert cannot reach a derived node ────────────────────

    [Fact]
    public void TheDerivedUpsertSetsNoMergeKeyProperties()
    {
        // THE guard. The write path MERGEs extracted facts on {subject_key, predicate_key, object_key,
        // owner_key} and FindByTriple looks them up the same way; a derived node carrying those four
        // could be matched by either.
        var cypher = DerivedFactQueries.UpsertDerived;

        cypher.Should().NotContain("f.subject_key");
        cypher.Should().NotContain("f.predicate_key");
        cypher.Should().NotContain("f.object_key");
        cypher.Should().NotContain("f.owner_key");
    }

    [Fact]
    public void TheDerivedUpsertStillSetsOwnerIdSoIsolationHolds()
    {
        // Omitting owner_key is safe only because isolation reads owner_id. Stated so a future
        // "cleanup" of one does not silently take the other.
        DerivedFactQueries.UpsertDerived.Should().Contain("f.owner_id = $ownerId");
    }

    [Fact]
    public void TheDerivedUpsertMergesOnTheDerivationKeyAlone()
    {
        DerivedFactQueries.UpsertDerived.Should().Contain(
            "MERGE (f:Fact {derivation_key: $derivationKey})",
            "an aggregate's value changes on every recompute, so triple identity would spawn a fresh "
            + "node per observation");
    }

    [Fact]
    public void TheDerivedUpsertReplacesItsProvenanceEdgesRatherThanAccumulatingThem()
    {
        // A derived value whose stated inputs no longer include the fact it was actually computed from
        // is worse than one with no provenance at all.
        var cypher = DerivedFactQueries.UpsertDerived;

        cypher.Should().Contain("OPTIONAL MATCH (f)-[old:DERIVED_FROM]->(:Fact)");
        cypher.Should().Contain("DELETE old");
    }

    [Fact]
    public void TheDerivedUpsertRefusesDerivedInputsSoTheDagStaysOneLevelDeep()
    {
        // Aggregating aggregates would make the cascade recursive, and a recursive cascade inside a
        // supersede statement is one that eventually gets moved out of the transaction.
        DerivedFactQueries.UpsertDerived.Should().Contain("coalesce(i.kind, '') <> 'derived'");
    }

    [Fact]
    public void TheDerivedUpsertReArmsAPreviouslyCascadedOutAggregate()
    {
        // What lets the cascade afford to be blunt: it invalidates on any input change, and the next
        // accountant pass restores whatever still holds.
        DerivedFactQueries.UpsertDerived.Should().Contain("f.invalidated_at = null");
    }

    // ── the group read ────────────────────────────────────────────────

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void TheGroupReadExcludesDerivedFacts(bool hasOwnerFilter, bool includeShared)
    {
        DerivedFactQueries.GetGroupFacts(hasOwnerFilter, includeShared)
            .Should().Contain("coalesce(f.kind, '') <> 'derived'");
    }

    [Fact]
    public void TheGroupReadOrdersByValidTimeThenTransactionTime()
    {
        // The order IS the arithmetic: a delta over an unordered group subtracts two arbitrary members
        // and reports the result as a change.
        DerivedFactQueries.GetGroupFacts(false, false)
            .Should().Contain("ORDER BY coalesce(f.valid_from, f.created_at) ASC");
    }

    [Fact]
    public void TheGroupReadIsDeterministicOnTies()
    {
        // Two facts sharing an instant would otherwise order arbitrarily, and "the latest value" would
        // differ between two runs over identical data.
        DerivedFactQueries.GetGroupFacts(false, false).Should().Contain("f.id ASC");
    }

    [Fact]
    public void TheGroupReadSeesOnlyLiveFacts()
    {
        DerivedFactQueries.GetGroupFacts(false, false).Should().Contain("f.invalidated_at IS NULL");
    }

    [Fact]
    public void TheGroupReadIsCapped()
    {
        DerivedFactQueries.GetGroupFacts(false, false).Should().Contain("LIMIT $limit");
    }

    [Fact]
    public void TheOwnerlessGroupReadAddsNoOwnerClause()
    {
        var cypher = DerivedFactQueries.GetGroupFacts(hasOwnerFilter: false, includeShared: false);

        cypher.Should().NotContain("owner_id = $ownerId");
    }

    [Fact]
    public void TheScopedGroupReadExcludesSharedWhenAskedTo()
    {
        var exclusive = DerivedFactQueries.GetGroupFacts(hasOwnerFilter: true, includeShared: false);
        var inclusive = DerivedFactQueries.GetGroupFacts(hasOwnerFilter: true, includeShared: true);

        exclusive.Should().NotContain("owner_id IS NULL");
        inclusive.Should().Contain("owner_id IS NULL");
    }
}
