using FluentAssertions;
using Neo4j.AgentMemory.Neo4j.Queries;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

public sealed class MetadataFilterBuilderTests
{
    // ── Null / empty ─────────────────────────────────────────────────────────

    [Fact]
    public void Build_NullFilters_ReturnsEmpty()
    {
        var (clause, parameters) = MetadataFilterBuilder.Build(null);

        clause.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void Build_EmptyFilters_ReturnsEmpty()
    {
        var (clause, parameters) = MetadataFilterBuilder.Build(new Dictionary<string, object>());

        clause.Should().BeEmpty();
        parameters.Should().BeEmpty();
    }

    // ── $eq ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_EqOperator_GeneratesEqualityClause()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.source"] = new Dictionary<string, object> { ["$eq"] = "slack" }
        };

        var (clause, parameters) = MetadataFilterBuilder.Build(filters);

        clause.Should().Contain("m.`metadata.source` = $filter_0");
        parameters["filter_0"].Should().Be("slack");
    }

    // ── $ne ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_NeOperator_GeneratesInequalityClause()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.status"] = new Dictionary<string, object> { ["$ne"] = "deleted" }
        };

        var (clause, parameters) = MetadataFilterBuilder.Build(filters);

        clause.Should().Contain("m.`metadata.status` <> $filter_0");
        parameters["filter_0"].Should().Be("deleted");
    }

    // ── $contains ────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ContainsOperator_GeneratesContainsClause()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.channel"] = new Dictionary<string, object> { ["$contains"] = "general" }
        };

        var (clause, parameters) = MetadataFilterBuilder.Build(filters);

        clause.Should().Contain("m.`metadata.channel` CONTAINS $filter_0");
        parameters["filter_0"].Should().Be("general");
    }

    // ── $in ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Build_InOperator_GeneratesInClause()
    {
        var values = new List<string> { "high", "critical" };
        var filters = new Dictionary<string, object>
        {
            ["metadata.priority"] = new Dictionary<string, object> { ["$in"] = values }
        };

        var (clause, parameters) = MetadataFilterBuilder.Build(filters);

        clause.Should().Contain("m.`metadata.priority` IN $filter_0");
        parameters["filter_0"].Should().Be(values);
    }

    // ── $exists ───────────────────────────────────────────────────────────────

    [Fact]
    public void Build_ExistsTrue_GeneratesIsNotNull()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.tag"] = new Dictionary<string, object> { ["$exists"] = true }
        };

        var (clause, parameters) = MetadataFilterBuilder.Build(filters);

        clause.Should().Contain("m.`metadata.tag` IS NOT NULL");
        parameters.Should().BeEmpty();
    }

    [Fact]
    public void Build_ExistsFalse_GeneratesIsNull()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.archived"] = new Dictionary<string, object> { ["$exists"] = false }
        };

        var (clause, parameters) = MetadataFilterBuilder.Build(filters);

        clause.Should().Contain("m.`metadata.archived` IS NULL");
        parameters.Should().BeEmpty();
    }

    // ── Combined filters ─────────────────────────────────────────────────────

    [Fact]
    public void Build_MultipleFilters_GeneratesAllClauses()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.source"]   = new Dictionary<string, object> { ["$eq"] = "slack" },
            ["metadata.priority"] = new Dictionary<string, object> { ["$in"] = new List<string> { "high", "critical" } },
            ["metadata.archived"] = new Dictionary<string, object> { ["$exists"] = false }
        };

        var (clause, parameters) = MetadataFilterBuilder.Build(filters);

        clause.Should().Contain("m.`metadata.source` = $filter_0");
        clause.Should().Contain("m.`metadata.priority` IN $filter_1");
        clause.Should().Contain("m.`metadata.archived` IS NULL");
        parameters.Should().HaveCount(2);
        parameters["filter_0"].Should().Be("slack");
    }

    // ── Custom node alias ─────────────────────────────────────────────────────

    [Fact]
    public void Build_CustomNodeAlias_UsesAliasInClauses()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.source"] = new Dictionary<string, object> { ["$eq"] = "teams" }
        };

        var (clause, _) = MetadataFilterBuilder.Build(filters, nodeAlias: "node");

        clause.Should().Contain("node.`metadata.source` = $filter_0");
        clause.Should().NotContain("m.`metadata.source`");
    }

    // ── Unsupported operator ──────────────────────────────────────────────────

    [Fact]
    public void Build_UnsupportedOperator_Throws()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.count"] = new Dictionary<string, object> { ["$gt"] = 5 }
        };

        var act = () => MetadataFilterBuilder.Build(filters);

        act.Should().Throw<NotSupportedException>().WithMessage("*$gt*");
    }

    // ── Injection prevention ──────────────────────────────────────────────────

    [Fact]
    public void Build_ValueContainsCypher_IsParameterizedNotInterpolated()
    {
        // A malicious value that, if interpolated, would break the query
        const string malicious = "slack' OR 1=1 //";
        var filters = new Dictionary<string, object>
        {
            ["metadata.source"] = new Dictionary<string, object> { ["$eq"] = malicious }
        };

        var (clause, parameters) = MetadataFilterBuilder.Build(filters);

        // The WHERE clause must NOT contain the raw value
        clause.Should().NotContain(malicious);
        // The value must be bound as a parameter
        parameters["filter_0"].Should().Be(malicious);
        // The clause must reference the parameter placeholder
        clause.Should().Contain("= $filter_0");
    }

    [Fact]
    public void Build_PropertyNameWithDot_IsBacktickQuotedInClause()
    {
        var filters = new Dictionary<string, object>
        {
            ["metadata.some.nested"] = new Dictionary<string, object> { ["$eq"] = "value" }
        };

        var (clause, _) = MetadataFilterBuilder.Build(filters);

        // Dot in property name must be backtick-quoted to be valid Cypher
        clause.Should().Contain("m.`metadata.some.nested`");
    }
}
