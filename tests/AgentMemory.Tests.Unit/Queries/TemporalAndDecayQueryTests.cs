using FluentAssertions;
using AgentMemory.Neo4j.Queries;

namespace AgentMemory.Tests.Unit.Queries;

public sealed class TemporalQueryTests
{
    // The get-by-id + recent-messages AsOf queries remain const strings.
    [Theory]
    [InlineData(nameof(TemporalQueries.GetRecentMessagesAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.GetEntityByIdAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.GetFactByIdAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.GetPreferenceByIdAsOf), "datetime($asOf)")]
    public void AllTemporalConstQueries_ContainAsOfFilter(string queryName, string expectedFragment)
    {
        var field = typeof(TemporalQueries).GetField(queryName);
        field.Should().NotBeNull($"TemporalQueries should have field {queryName}");

        var query = (string)field!.GetValue(null)!;
        query.Should().Contain(expectedFragment);
    }

    // The scoped vector AsOf searches are now methods (IC5): they over-fetch + filter by owner.
    // They are bitemporal (D6): the transaction clock binds created_at/invalidated_at via $systemAsOf.
    [Fact]
    public void TemporalSearchMethods_ContainAsOfFilterAndEmbeddingIndex()
    {
        foreach (var q in new[]
        {
            TemporalQueries.SearchEntitiesAsOf(hasOwnerFilter: false, includeShared: true, topK: 10),
            TemporalQueries.SearchFactsAsOf(hasOwnerFilter: false, includeShared: true, topK: 10),
            TemporalQueries.SearchPreferencesAsOf(hasOwnerFilter: false, includeShared: true, topK: 10),
        })
        {
            q.Should().Contain("datetime($systemAsOf)");
            q.Should().Contain("db.index.vector.queryNodes");
        }
    }

    [Fact]
    public void SearchEntitiesAsOf_FiltersCreatedAtAndInvalidatedAt()
    {
        TemporalQueries.SearchEntitiesAsOf(hasOwnerFilter: false, includeShared: true, topK: 10)
            .Should().Contain("node.created_at <= datetime($systemAsOf)")
            .And.Contain("node.invalidated_at IS NULL OR node.invalidated_at > datetime($systemAsOf)");
    }

    [Fact]
    public void SearchEntitiesAsOf_Scoped_AppliesOwnerFilter()
    {
        TemporalQueries.SearchEntitiesAsOf(hasOwnerFilter: true, includeShared: true, topK: 50)
            .Should().Contain("node.owner_id = $ownerId OR node.owner_id IS NULL");
        TemporalQueries.SearchEntitiesAsOf(hasOwnerFilter: false, includeShared: true, topK: 10)
            .Should().NotContain("owner_id");
    }

    [Fact]
    public void SearchFactsAsOf_FiltersValidityWindow()
    {
        // D6: the fact's validity window is bound by the valid-time clock ($validAsOf), distinct from the
        // transaction clock ($systemAsOf) that binds created_at/invalidated_at.
        TemporalQueries.SearchFactsAsOf(hasOwnerFilter: false, includeShared: true, topK: 10)
            .Should().Contain("node.valid_from IS NULL OR node.valid_from <= datetime($validAsOf)")
            .And.Contain("node.valid_until IS NULL OR node.valid_until > datetime($validAsOf)");
    }

    [Fact]
    public void GetRecentMessagesAsOf_FiltersTimestamp()
    {
        TemporalQueries.GetRecentMessagesAsOf
            .Should().Contain("m.timestamp <= datetime($asOf)")
            .And.Contain("$sessionId")
            .And.Contain("$limit");
    }
}

public sealed class DecayQueryTests
{
    [Theory]
    [InlineData("Entity")]
    [InlineData("Fact")]
    [InlineData("Preference")]
    public void UpdateAccessTimestamp_ContainsLabelAndSetClause(string label)
    {
        var query = DecayQueries.UpdateAccessTimestamp(label);

        query.Should().Contain($":{label}")
            .And.Contain("last_accessed_at")
            .And.Contain("access_count");
    }

    [Theory]
    [InlineData("Entity")]
    [InlineData("Fact")]
    [InlineData("Preference")]
    public void GetRetentionFields_ContainsRequiredFields(string label)
    {
        var query = DecayQueries.GetRetentionFields(label);

        query.Should().Contain($":{label}")
            .And.Contain("confidence")
            .And.Contain("createdAt")
            .And.Contain("lastAccessedAt")
            .And.Contain("accessCount");
    }

    [Fact]
    public void PruneEntities_ContainsDecayFormula()
    {
        DecayQueries.PruneEntities(hasOwnerFilter: false)
            .Should().Contain("exp(-$lambda")
            .And.Contain("$minScore")
            .And.Contain("DETACH DELETE");
    }

    [Fact]
    public void PruneFacts_ContainsDecayFormula()
    {
        DecayQueries.PruneFacts(hasOwnerFilter: false)
            .Should().Contain("exp(-$lambda")
            .And.Contain("$minScore")
            .And.Contain("DETACH DELETE");
    }

    [Fact]
    public void PrunePreferences_ContainsDecayFormula()
    {
        DecayQueries.PrunePreferences(hasOwnerFilter: false)
            .Should().Contain("exp(-$lambda")
            .And.Contain("$minScore")
            .And.Contain("DETACH DELETE");
    }

    [Theory]
    [InlineData("e")]
    public void PruneEntities_Scoped_AppliesOwnerFilter(string alias)
    {
        DecayQueries.PruneEntities(hasOwnerFilter: true)
            .Should().Contain($"{alias}.owner_id = $ownerId");
        DecayQueries.PruneEntities(hasOwnerFilter: false)
            .Should().NotContain("owner_id");
    }

    // ── D4: non-destructive prune (soft-invalidate) ────────────────────

    public static IEnumerable<object[]> AllPrune()
    {
        yield return new object[] { "Entity", (Func<bool, bool, string>)DecayQueries.PruneEntities };
        yield return new object[] { "Fact", (Func<bool, bool, string>)DecayQueries.PruneFacts };
        yield return new object[] { "Preference", (Func<bool, bool, string>)DecayQueries.PrunePreferences };
    }

    [Theory]
    [MemberData(nameof(AllPrune))]
    public void Prune_NonDestructive_SoftInvalidates_AndSkipsAlreadyInvalidated(string label, Func<bool, bool, string> build)
    {
        var cypher = build(/*hasOwnerFilter*/ false, /*nonDestructive*/ true);

        cypher.Should().Contain("SET").And.Contain("invalidated_at = datetime($now)");
        cypher.Should().NotContain("DETACH DELETE", $"{label} non-destructive prune must not delete");
        cypher.Should().Contain("invalidated_at IS NULL",
            $"{label} non-destructive prune must skip already-invalidated nodes so a re-run does not re-count them");
    }

    [Theory]
    [MemberData(nameof(AllPrune))]
    public void Prune_Destructive_HardDeletes(string label, Func<bool, bool, string> build)
    {
        var cypher = build(/*hasOwnerFilter*/ false, /*nonDestructive*/ false);

        cypher.Should().Contain("DETACH DELETE", $"{label} destructive prune hard-deletes");
        cypher.Should().NotContain("invalidated_at");
    }

    [Fact]
    public void PruneFactsAndPreferences_Scoped_AppliesOwnerFilter()
    {
        DecayQueries.PruneFacts(hasOwnerFilter: true).Should().Contain("f.owner_id = $ownerId");
        DecayQueries.PruneFacts(hasOwnerFilter: false).Should().NotContain("owner_id");
        DecayQueries.PrunePreferences(hasOwnerFilter: true).Should().Contain("p.owner_id = $ownerId");
        DecayQueries.PrunePreferences(hasOwnerFilter: false).Should().NotContain("owner_id");
    }
}
