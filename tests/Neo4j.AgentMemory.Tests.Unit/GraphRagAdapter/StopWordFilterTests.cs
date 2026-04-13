using FluentAssertions;
using Neo4j.AgentMemory.GraphRagAdapter.Internal;

namespace Neo4j.AgentMemory.Tests.Unit.GraphRagAdapter;

public sealed class StopWordFilterTests
{
    [Fact]
    public void ExtractKeywords_EmptyString_ReturnsEmpty()
    {
        var result = StopWordFilter.ExtractKeywords("");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractKeywords_OnlyStopWords_ReturnsEmpty()
    {
        var result = StopWordFilter.ExtractKeywords("what is the");

        result.Should().BeEmpty();
    }

    [Fact]
    public void ExtractKeywords_MixedContent_RemovesStopWordsPreservesKeywords()
    {
        var result = StopWordFilter.ExtractKeywords("what is Neo4j");

        result.Should().Be("neo4j");
    }

    [Fact]
    public void ExtractKeywords_NoStopWords_ReturnsAllWords()
    {
        var result = StopWordFilter.ExtractKeywords("Neo4j graph database");

        result.Should().Be("neo4j graph database");
    }

    [Fact]
    public void ExtractKeywords_SingleCharWords_AreFiltered()
    {
        // Single-char words are filtered by the length > 1 rule
        var result = StopWordFilter.ExtractKeywords("a b c neo4j");

        result.Should().Be("neo4j");
    }

    [Fact]
    public void ExtractKeywords_IsCaseInsensitive_ForStopWords()
    {
        var result = StopWordFilter.ExtractKeywords("WHERE IS THE database");

        result.Should().Be("database");
    }

    [Fact]
    public void ExtractKeywords_OutputIsLowercase()
    {
        var result = StopWordFilter.ExtractKeywords("Neo4j Cypher Query");

        result.Should().Be("neo4j cypher query");
    }

    [Fact]
    public void ExtractKeywords_ComplexSentence_ExtractsOnlyMeaningfulTerms()
    {
        var result = StopWordFilter.ExtractKeywords("find all entities related to london");

        // "find", "all", "related", "to" are stop words; "entities" and "london" should remain
        result.Should().Be("entities london");
    }
}
