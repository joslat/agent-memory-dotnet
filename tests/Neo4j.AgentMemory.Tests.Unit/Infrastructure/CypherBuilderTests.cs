using FluentAssertions;
using Neo4j.AgentMemory.Neo4j.Infrastructure;

namespace Neo4j.AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// Unit tests for <see cref="CypherBuilder"/>.
/// </summary>
public sealed class CypherBuilderTests
{
    // ── Basic composition ────────────────────────────────────────────────────

    [Fact]
    public void Match_Return_ProducesBasicQuery()
    {
        var cypher = CypherBuilder.Match("(n:Message {id: $id})")
            .Return("n")
            .Build();

        cypher.Should().Be(
            "MATCH (n:Message {id: $id})" + Environment.NewLine +
            "RETURN n");
    }

    [Fact]
    public void Call_ProducesCallClause()
    {
        var cypher = CypherBuilder.Call("apoc.util.sleep(100)")
            .Return("true AS done")
            .Build();

        cypher.Should().Contain("CALL apoc.util.sleep(100)");
        cypher.Should().Contain("RETURN true AS done");
    }

    [Fact]
    public void With_AppendsWithClause()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .With("n, n.name AS name")
            .Return("name")
            .Build();

        cypher.Should().Contain("WITH n, n.name AS name");
    }

    [Fact]
    public void Unwind_AppendsUnwindClause()
    {
        var cypher = CypherBuilder.Match("(n)")
            .Unwind("$ids AS id")
            .Return("n")
            .Build();

        cypher.Should().Contain("UNWIND $ids AS id");
    }

    [Fact]
    public void Set_AppendsSetClause()
    {
        var cypher = CypherBuilder.Match("(n:Entity {id: $id})")
            .Set("n.updated_at = datetime()")
            .Return("n")
            .Build();

        cypher.Should().Contain("SET n.updated_at = datetime()");
    }

    [Fact]
    public void OptionalMatch_AppendsOptionalMatchClause()
    {
        var cypher = CypherBuilder.Match("(c:Conversation {id: $id})")
            .OptionalMatch("(c)-[:HAS_MESSAGE]->(m:Message)")
            .Return("c, m")
            .Build();

        cypher.Should().Contain("OPTIONAL MATCH (c)-[:HAS_MESSAGE]->(m:Message)");
    }

    // ── WHERE smart logic ────────────────────────────────────────────────────

    [Fact]
    public void FirstWhere_EmitsWhereKeyword()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .Where("n.type = $type")
            .Return("n")
            .Build();

        cypher.Should().Contain("WHERE n.type = $type");
        cypher.Should().NotContain("AND n.type");
    }

    [Fact]
    public void SecondWhere_EmitsAndKeyword()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .Where("n.type = $type")
            .Where("n.confidence > $minConf")
            .Return("n")
            .Build();

        cypher.Should().Contain("WHERE n.type = $type");
        cypher.Should().Contain("AND n.confidence > $minConf");
    }

    [Fact]
    public void And_EmitsAndKeyword()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .Where("n.type = $type")
            .And("n.name = $name")
            .Return("n")
            .Build();

        cypher.Should().Contain("WHERE n.type = $type");
        cypher.Should().Contain("AND n.name = $name");
    }

    [Fact]
    public void And_WhenNoWhereStarted_EmitsWhereKeyword()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .And("n.name = $name")
            .Return("n")
            .Build();

        cypher.Should().Contain("WHERE n.name = $name");
        cypher.Should().NotContain("AND n.name");
    }

    [Fact]
    public void Or_EmitsOrKeyword()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .Where("n.type = $type")
            .Or("n.name = $name")
            .Return("n")
            .Build();

        cypher.Should().Contain("WHERE n.type = $type");
        cypher.Should().Contain("OR n.name = $name");
    }

    [Fact]
    public void Or_WhenNoWhereStarted_EmitsWhereKeyword()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .Or("n.type = $type")
            .Return("n")
            .Build();

        cypher.Should().Contain("WHERE n.type = $type");
        cypher.Should().NotContain("OR n.type");
    }

    // ── Conditional (when:) logic ────────────────────────────────────────────

    [Fact]
    public void Where_WhenFalse_IsOmitted()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .Where("n.type = $type", when: false)
            .Return("n")
            .Build();

        cypher.Should().NotContain("WHERE");
        cypher.Should().NotContain("n.type");
    }

    [Fact]
    public void And_WhenFalse_IsOmitted()
    {
        var cypher = CypherBuilder.Match("(n:Entity)")
            .Where("n.name = $name")
            .And("n.type = $type", when: false)
            .Return("n")
            .Build();

        cypher.Should().Contain("WHERE n.name = $name");
        cypher.Should().NotContain("AND");
        cypher.Should().NotContain("n.type");
    }

    [Fact]
    public void AllConditionsFalse_ProducesBareMatchReturn()
    {
        var cypher = CypherBuilder.Match("(e:Entity)")
            .Where("e.type = $type", when: false)
            .And("e.name = $name", when: false)
            .Return("e")
            .Build();

        cypher.Should().Be(
            "MATCH (e:Entity)" + Environment.NewLine +
            "RETURN e");
    }

    [Fact]
    public void NoWhereConditions_OmitsWhereKeyword()
    {
        var cypher = CypherBuilder.Match("(e:Entity)")
            .Return("e")
            .Build();

        cypher.Should().NotContain("WHERE");
    }

    // ── Pagination and ordering ──────────────────────────────────────────────

    [Fact]
    public void OrderBy_AppendsOrderByClause()
    {
        var cypher = CypherBuilder.Match("(m:Message)")
            .Return("m")
            .OrderBy("m.timestamp DESC")
            .Build();

        cypher.Should().Contain("ORDER BY m.timestamp DESC");
    }

    [Fact]
    public void OrderBy_WhenFalse_IsOmitted()
    {
        var cypher = CypherBuilder.Match("(m:Message)")
            .Return("m")
            .OrderBy("m.timestamp DESC", when: false)
            .Build();

        cypher.Should().NotContain("ORDER BY");
    }

    [Fact]
    public void Limit_AppendsLimitClause()
    {
        var cypher = CypherBuilder.Match("(m:Message)")
            .Return("m")
            .Limit("$limit")
            .Build();

        cypher.Should().Contain("LIMIT $limit");
    }

    [Fact]
    public void Limit_WhenFalse_IsOmitted()
    {
        var cypher = CypherBuilder.Match("(m:Message)")
            .Return("m")
            .Limit("$limit", when: false)
            .Build();

        cypher.Should().NotContain("LIMIT");
    }

    [Fact]
    public void Skip_AppendsSkipClause()
    {
        var cypher = CypherBuilder.Match("(m:Message)")
            .Return("m")
            .Skip("$skip")
            .Build();

        cypher.Should().Contain("SKIP $skip");
    }

    [Fact]
    public void Skip_WhenFalse_IsOmitted()
    {
        var cypher = CypherBuilder.Match("(m:Message)")
            .Return("m")
            .Skip("$skip", when: false)
            .Build();

        cypher.Should().NotContain("SKIP");
    }

    // ── Vector search ────────────────────────────────────────────────────────

    [Fact]
    public void WithVectorSearch_GeneratesCallAndYield()
    {
        var cypher = new CypherBuilder()
            .WithVectorSearch("entity_embedding_idx", "$embedding", "node", 10)
            .Return("node, score")
            .Build();

        cypher.Should().Contain("CALL db.index.vector.queryNodes('entity_embedding_idx', 10, $embedding)");
        cypher.Should().Contain("YIELD node, score");
        cypher.Should().Contain("RETURN node, score");
    }

    [Fact]
    public void WithVectorSearch_TopKIsEmbeddedAsLiteral()
    {
        var cypher = new CypherBuilder()
            .WithVectorSearch("fact_embedding_idx", "$embedding", "node", 25)
            .Return("node, score")
            .Build();

        cypher.Should().Contain(", 25, ");
        cypher.Should().NotContain("$topK");
    }

    [Fact]
    public void WithVectorSearch_WithConditionalSessionFilter()
    {
        string? sessionId = "session-abc";

        var cypher = new CypherBuilder()
            .WithVectorSearch("message_embedding_idx", "$embedding", "node", 10)
            .Where("score >= $minScore")
            .And("node.session_id = $sessionId", when: sessionId is not null)
            .Return("node, score")
            .OrderBy("score DESC")
            .Build();

        cypher.Should().Contain("WHERE score >= $minScore");
        cypher.Should().Contain("AND node.session_id = $sessionId");
        cypher.Should().Contain("RETURN node, score");
        cypher.Should().Contain("ORDER BY score DESC");
    }

    [Fact]
    public void WithVectorSearch_SessionFilterOmittedWhenNull()
    {
        string? sessionId = null;

        var cypher = new CypherBuilder()
            .WithVectorSearch("message_embedding_idx", "$embedding", "node", 10)
            .Where("score >= $minScore")
            .And("node.session_id = $sessionId", when: sessionId is not null)
            .Return("node, score")
            .OrderBy("score DESC")
            .Build();

        cypher.Should().Contain("WHERE score >= $minScore");
        cypher.Should().NotContain("session_id");
        cypher.Should().NotContain("AND");
    }

    // ── AndRawFragment ──────────────────────────────────────────────────────

    [Fact]
    public void AndRawFragment_AppendsLinesVerbatim()
    {
        const string fragment = "AND node.`metadata.source` = $filter_0\nAND node.`metadata.type` = $filter_1";

        var cypher = CypherBuilder.Match("(node:Message)")
            .Where("node.session_id = $sessionId")
            .AndRawFragment(fragment)
            .Return("node")
            .Build();

        cypher.Should().Contain("AND node.`metadata.source` = $filter_0");
        cypher.Should().Contain("AND node.`metadata.type` = $filter_1");
    }

    [Fact]
    public void AndRawFragment_EmptyFragment_IsSkipped()
    {
        var cypher = CypherBuilder.Match("(node:Message)")
            .Where("node.session_id = $sessionId")
            .AndRawFragment(string.Empty)
            .Return("node")
            .Build();

        var lines = cypher.Split(Environment.NewLine);
        lines.Should().HaveCount(3); // MATCH, WHERE, RETURN
    }

    [Fact]
    public void AndRawFragment_NullFragment_IsSkipped()
    {
        var cypher = CypherBuilder.Match("(node:Message)")
            .Where("node.session_id = $sessionId")
            .AndRawFragment(null)
            .Return("node")
            .Build();

        cypher.Should().NotContain("null");
        cypher.Should().Contain("WHERE node.session_id = $sessionId");
    }

    [Fact]
    public void AndRawFragment_WhenFalse_IsSkipped()
    {
        var cypher = CypherBuilder.Match("(node:Message)")
            .Where("node.session_id = $sessionId")
            .AndRawFragment("AND node.extra = $extra", when: false)
            .Return("node")
            .Build();

        cypher.Should().NotContain("extra");
    }

    // ── Immutability / thread safety ─────────────────────────────────────────

    [Fact]
    public void Builder_IsImmutable_BranchingProducesIndependentInstances()
    {
        var base_builder = CypherBuilder.Match("(e:Entity)");

        var withType = base_builder.Where("e.type = $type").Return("e");
        var withName = base_builder.Where("e.name = $name").Return("e");

        withType.Build().Should().Contain("e.type = $type");
        withType.Build().Should().NotContain("e.name");

        withName.Build().Should().Contain("e.name = $name");
        withName.Build().Should().NotContain("e.type");
    }

    // ── Snapshot: full query composition ────────────────────────────────────

    [Fact]
    public void Build_ProducesCorrectNewlineSeparatedString()
    {
        var cypher = CypherBuilder.Match("(e:Entity)")
            .Where("e.type = $type")
            .And("e.confidence >= $minConf")
            .Return("e")
            .OrderBy("e.name")
            .Limit("$limit")
            .Build();

        var expected =
            "MATCH (e:Entity)" + Environment.NewLine +
            "WHERE e.type = $type" + Environment.NewLine +
            "AND e.confidence >= $minConf" + Environment.NewLine +
            "RETURN e" + Environment.NewLine +
            "ORDER BY e.name" + Environment.NewLine +
            "LIMIT $limit";

        cypher.Should().Be(expected);
    }

    [Fact]
    public void EmptyBuilder_BuildReturnsEmptyString()
    {
        var cypher = new CypherBuilder().Build();
        cypher.Should().BeEmpty();
    }
}
