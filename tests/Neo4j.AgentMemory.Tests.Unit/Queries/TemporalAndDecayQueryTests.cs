using FluentAssertions;
using Neo4j.AgentMemory.Neo4j.Queries;

namespace Neo4j.AgentMemory.Tests.Unit.Queries;

public sealed class TemporalQueryTests
{
    [Theory]
    [InlineData(nameof(TemporalQueries.SearchEntitiesAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.SearchFactsAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.SearchPreferencesAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.GetRecentMessagesAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.GetEntityByIdAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.GetFactByIdAsOf), "datetime($asOf)")]
    [InlineData(nameof(TemporalQueries.GetPreferenceByIdAsOf), "datetime($asOf)")]
    public void AllTemporalQueries_ContainAsOfFilter(string queryName, string expectedFragment)
    {
        var field = typeof(TemporalQueries).GetField(queryName);
        field.Should().NotBeNull($"TemporalQueries should have field {queryName}");

        var query = (string)field!.GetValue(null)!;
        query.Should().Contain(expectedFragment);
    }

    [Theory]
    [InlineData(nameof(TemporalQueries.SearchEntitiesAsOf))]
    [InlineData(nameof(TemporalQueries.SearchFactsAsOf))]
    [InlineData(nameof(TemporalQueries.SearchPreferencesAsOf))]
    public void VectorSearchQueries_ContainEmbeddingIndex(string queryName)
    {
        var query = (string)typeof(TemporalQueries).GetField(queryName)!.GetValue(null)!;
        query.Should().Contain("db.index.vector.queryNodes");
    }

    [Fact]
    public void SearchEntitiesAsOf_FiltersCreatedAtAndInvalidatedAt()
    {
        TemporalQueries.SearchEntitiesAsOf
            .Should().Contain("node.created_at <= datetime($asOf)")
            .And.Contain("node.invalidated_at IS NULL OR node.invalidated_at > datetime($asOf)");
    }

    [Fact]
    public void SearchFactsAsOf_FiltersValidityWindow()
    {
        TemporalQueries.SearchFactsAsOf
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
