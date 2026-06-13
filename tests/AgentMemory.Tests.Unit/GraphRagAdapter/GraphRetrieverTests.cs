using FluentAssertions;
using Microsoft.Extensions.AI;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Retrieval.Internal;
using AgentMemory.Neo4j.Services;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.GraphRagAdapter;

/// <summary>
/// Unit coverage for task 3.4: a first-class <see cref="GraphRetriever"/> and a
/// selection-only <c>CreateRetriever</c>. Traversal against a live graph is exercised by the
/// integration suite (requires Neo4j); these tests pin construction, selection, the generated
/// Cypher shape, and record formatting.
/// </summary>
public sealed class GraphRetrieverTests
{
    private static readonly IDriver Driver = Substitute.For<IDriver>();
    private static readonly IEmbeddingGenerator<string, Embedding<float>> Generator =
        Substitute.For<IEmbeddingGenerator<string, Embedding<float>>>();

    // ---- CreateRetriever is pure selection (no mode-specific behavior beyond picking a type) ----

    [Theory]
    [InlineData(GraphRagSearchMode.Vector, typeof(VectorRetriever))]
    [InlineData(GraphRagSearchMode.Fulltext, typeof(FulltextRetriever))]
    [InlineData(GraphRagSearchMode.Hybrid, typeof(HybridRetriever))]
    [InlineData(GraphRagSearchMode.Graph, typeof(GraphRetriever))]
    public void CreateRetriever_SelectsRetrieverForMode(GraphRagSearchMode mode, Type expected)
    {
        var options = new GraphRagOptions
        {
            IndexName = "idx",
            FulltextIndexName = "ft_idx",
            SearchMode = mode
        };

        var retriever = Neo4jGraphRagContextSource.CreateRetriever(Driver, Generator, options);

        retriever.Should().BeOfType(expected);
    }

    [Fact]
    public void CreateRetriever_GraphMode_DoesNotDependOnRetrievalQuery()
    {
        // Graph mode must build a GraphRetriever even when no RetrievalQuery is configured —
        // the old implementation fell back to an inline raw Cypher tail; the new one does not.
        var options = new GraphRagOptions
        {
            IndexName = "idx",
            SearchMode = GraphRagSearchMode.Graph,
            RetrievalQuery = null
        };

        Neo4jGraphRagContextSource.CreateRetriever(Driver, Generator, options)
            .Should().BeOfType<GraphRetriever>();
    }

    // ---- Constructor validation of the interpolated hop bound ----

    [Theory]
    [InlineData(0)]
    [InlineData(6)]
    [InlineData(-1)]
    public void Ctor_RejectsOutOfRangeHops(int hops)
    {
        var act = () => new GraphRetriever(Driver, "idx", Generator, hops);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void Ctor_AcceptsInRangeHops(int hops)
    {
        var act = () => new GraphRetriever(Driver, "idx", Generator, hops);
        act.Should().NotThrow();
    }

    // ---- Generated traversal Cypher ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void BuildTraversalCypher_InterpolatesHopBoundAsLiteral(int hops)
    {
        var cypher = GraphRetriever.BuildTraversalCypher(hops);

        cypher.Should().Contain($"RELATED_TO*1..{hops}");
        // Hops are a literal, never a parameter (Cypher forbids parameterizing path bounds).
        cypher.Should().NotContain("$hops");
        cypher.Should().NotContain("$maxTraversalHops");
        // Real vector-seed + traversal, not a vector-only query.
        cypher.Should().Contain("db.index.vector.queryNodes");
        cypher.Should().Contain("MATCH path = (seed)-[:RELATED_TO");
        // Default γ = 1.0 ⇒ no structural decay ⇒ today's ordering (raw score, hops as tiebreaker).
        cypher.Should().NotContain("structScore");
        cypher.Should().NotContain("$gamma");
        cypher.Should().Contain("ORDER BY score DESC, hops ASC");
    }

    // ---- D2: structural hop decay (score · γ^hops) ----

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    public void BuildTraversalCypher_AppliesStructuralDecay_WhenGammaBelowOne(int hops)
    {
        var cypher = GraphRetriever.BuildTraversalCypher(hops, scoped: false, gamma: 0.7);

        cypher.Should().Contain("score * ($gamma ^ hops) AS structScore",
            "γ < 1 must decay the seed score per hop");
        cypher.Should().Contain("ORDER BY structScore DESC, hops ASC");
        cypher.Should().Contain($"RELATED_TO*1..{hops}");
        cypher.Should().NotContain("$hops");
    }

    [Fact]
    public void BuildTraversalCypher_GammaOne_IsByteForByteTheOffPath()
    {
        GraphRetriever.BuildTraversalCypher(2, scoped: false, gamma: 1.0)
            .Should().Be(GraphRetriever.BuildTraversalCypher(2, scoped: false));
    }

    [Theory]
    [InlineData(1.0)]   // no decay branch
    [InlineData(0.7)]   // structural-decay branch
    public void BuildTraversalCypher_Scoped_FiltersTheWholePath_NotJustEndpoints(double gamma)
    {
        // R1 hardening: a scoped graph walk must not route THROUGH another owner's node — every node on
        // the path (seed, intermediates, related) is owner/shared-filtered, not only the endpoints.
        var scoped = GraphRetriever.BuildTraversalCypher(2, scoped: true, gamma: gamma);
        scoped.Should().Contain("all(n IN nodes(path) WHERE n.owner_id = $owner_id OR n.owner_id IS NULL)");
        scoped.Should().NotContain("related.owner_id", "the endpoint-only filter is subsumed by the path-wide one");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.7)]
    public void BuildTraversalCypher_Unscoped_HasNoOwnerFilterOnThePath(double gamma)
    {
        GraphRetriever.BuildTraversalCypher(2, scoped: false, gamma: gamma)
            .Should().NotContain("owner_id").And.NotContain("nodes(path)");
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void Ctor_ToleratesOutOfRangeGamma_TreatsAsOff(double gamma)
    {
        // γ outside (0,1] is meaningless for the power; the retriever treats it as off rather than throwing.
        var act = () => new GraphRetriever(Driver, "idx", Generator, 2, gamma);
        act.Should().NotThrow();
    }

    // ---- Record formatting ----

    [Fact]
    public void FormatGraphResult_CombinesSeedAndRelatedWithScoreAndHops()
    {
        var record = Substitute.For<IRecord>();
        record["seedText"].Returns("Alice");
        record["relatedText"].Returns("Acme Corp");
        record["score"].Returns(0.91d);
        record["hops"].Returns(2);

        var item = GraphRetriever.FormatGraphResult(record);

        item.Content.Should().Be("Alice → Acme Corp");
        item.Metadata.Should().NotBeNull();
        item.Metadata!["score"].Should().Be(0.91d);
        item.Metadata!["hops"].Should().Be(2);
    }

    [Fact]
    public void FormatGraphResult_FallsBackToSeedWhenNoRelatedText()
    {
        var record = Substitute.For<IRecord>();
        record["seedText"].Returns("Alice");
        record["relatedText"].Returns(string.Empty);
        record["score"].Returns(0.5d);
        record["hops"].Returns(1);

        var item = GraphRetriever.FormatGraphResult(record);

        item.Content.Should().Be("Alice");
    }
}
