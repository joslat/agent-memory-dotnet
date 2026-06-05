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
            q.Should().Contain("datetime($asOf)");
            q.Should().Contain("db.index.vector.queryNodes");
        }
    }

    [Fact]
    public void SearchEntitiesAsOf_FiltersCreatedAtAndInvalidatedAt()
    {
        TemporalQueries.SearchEntitiesAsOf(hasOwnerFilter: false, includeShared: true, topK: 10)
            .Should().Contain("node.created_at <= datetime($asOf)")
            .And.Contain("node.invalidated_at IS NULL OR node.invalidated_at > datetime($asOf)");
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
        TemporalQueries.SearchFactsAsOf(hasOwnerFilter: false, includeShared: true, topK: 10)
            .Should().Contain("node.valid_from IS NULL OR node.valid_from <= datetime($asOf)")
            .And.Contain("node.valid_until IS NULL OR node.valid_until > datetime($asOf)");
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
        DecayQueries.PruneEntities
            .Should().Contain("exp(-$lambda")
            .And.Contain("$minScore")
            .And.Contain("DETACH DELETE");
    }

    [Fact]
    public void PruneFacts_ContainsDecayFormula()
    {
        DecayQueries.PruneFacts
            .Should().Contain("exp(-$lambda")
            .And.Contain("$minScore")
            .And.Contain("DETACH DELETE");
    }

    [Fact]
    public void PrunePreferences_ContainsDecayFormula()
    {
        DecayQueries.PrunePreferences
            .Should().Contain("exp(-$lambda")
            .And.Contain("$minScore")
            .And.Contain("DETACH DELETE");
    }
}
