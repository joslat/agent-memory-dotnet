using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Neo4j.Retrieval;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Services;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Neo4j.AgentMemory.Tests.Unit.GraphRagAdapter;

public sealed class Neo4jGraphRagContextSourceTests
{
    private static readonly GraphRagOptions DefaultOptions = new()
    {
        IndexName = "test_vector_index",
        FulltextIndexName = "test_fulltext_index",
        TopK = 5,
        SearchMode = GraphRagSearchMode.Hybrid
    };

    private static Neo4jGraphRagContextSource CreateSut(
        IRetriever retriever,
        GraphRagOptions? options = null)
    {
        return new Neo4jGraphRagContextSource(
            retriever,
            options ?? DefaultOptions,
            NullLogger<Neo4jGraphRagContextSource>.Instance);
    }

    private static GraphRagContextRequest MakeRequest(
        string query = "test query", int topK = 3) =>
        new() { SessionId = "session-1", Query = query, TopK = topK };

    // -------------------------------------------------------------------------
    // Result mapping
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetContext_MapsContentToText()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([new RetrieverResultItem("hello world")]));

        var sut = CreateSut(retriever);
        var result = await sut.GetContextAsync(MakeRequest());

        result.Items.Should().HaveCount(1);
        result.Items[0].Text.Should().Be("hello world");
    }

    [Fact]
    public async Task GetContext_MapsScoreFromMetadata()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([
                new RetrieverResultItem("text", new Dictionary<string, object?> { ["score"] = 0.87 })
            ]));

        var sut = CreateSut(retriever);
        var result = await sut.GetContextAsync(MakeRequest());

        result.Items[0].Score.Should().BeApproximately(0.87, 0.0001);
    }

    [Fact]
    public async Task GetContext_MapsAdditionalMetadata()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([
                new RetrieverResultItem("text", new Dictionary<string, object?>
                {
                    ["score"] = 0.5,
                    ["source"] = "article-42",
                    ["rank"] = 1L
                })
            ]));

        var sut = CreateSut(retriever);
        var result = await sut.GetContextAsync(MakeRequest());

        var item = result.Items[0];
        item.Metadata.Should().ContainKey("source").WhoseValue.Should().Be("article-42");
        item.Metadata.Should().ContainKey("rank").WhoseValue.Should().Be(1L);
        item.Metadata.Should().NotContainKey("score"); // score is promoted, not in metadata
    }

    [Fact]
    public async Task GetContext_NullMetadata_ScoreIsZero()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([new RetrieverResultItem("no meta", null)]));

        var sut = CreateSut(retriever);
        var result = await sut.GetContextAsync(MakeRequest());

        result.Items[0].Score.Should().Be(0);
        result.Items[0].Metadata.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // TopK forwarding
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetContext_RespectsTopKFromRequest()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([]));

        var sut = CreateSut(retriever);
        await sut.GetContextAsync(MakeRequest(topK: 7));

        await retriever.Received(1).SearchAsync(Arg.Any<string>(), 7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetContext_UsesOptionsTopK_WhenRequestTopKIsZero()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([]));

        var options = new GraphRagOptions { IndexName = "idx", TopK = 10 };
        var sut = CreateSut(retriever, options);
        var request = new GraphRagContextRequest { SessionId = "s", Query = "q", TopK = 0 };
        await sut.GetContextAsync(request);

        await retriever.Received(1).SearchAsync(Arg.Any<string>(), 10, Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Empty / error paths
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetContext_EmptyResults_ReturnsEmptyItems()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([]));

        var sut = CreateSut(retriever);
        var result = await sut.GetContextAsync(MakeRequest());

        result.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetContext_RetrieverThrows_ReturnsEmptyWithoutRethrow()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("neo4j offline"));

        var sut = CreateSut(retriever);
        var result = await sut.GetContextAsync(MakeRequest());

        result.Items.Should().BeEmpty();
    }

    // -------------------------------------------------------------------------
    // Query text forwarding
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetContext_ForwardsQueryTextToRetriever()
    {
        const string query = "What is graph RAG?";
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([]));

        var sut = CreateSut(retriever);
        await sut.GetContextAsync(new GraphRagContextRequest
        {
            SessionId = "s",
            Query = query,
            TopK = 3
        });

        await retriever.Received(1).SearchAsync(query, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    // -------------------------------------------------------------------------
    // Multiple items
    // -------------------------------------------------------------------------

    [Fact]
    public async Task GetContext_MultipleItems_AllMapped()
    {
        var retriever = Substitute.For<IRetriever>();
        retriever.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new RetrieverResult([
                new RetrieverResultItem("item1", new Dictionary<string, object?> { ["score"] = 0.9 }),
                new RetrieverResultItem("item2", new Dictionary<string, object?> { ["score"] = 0.7 }),
                new RetrieverResultItem("item3", new Dictionary<string, object?> { ["score"] = 0.5 })
            ]));

        var sut = CreateSut(retriever);
        var result = await sut.GetContextAsync(MakeRequest());

        result.Items.Should().HaveCount(3);
        result.Items.Select(i => i.Text).Should().Equal("item1", "item2", "item3");
        result.Items.Select(i => i.Score).Should().BeEquivalentTo(new[] { 0.9, 0.7, 0.5 });
    }

    // -------------------------------------------------------------------------
    // Options defaults
    // -------------------------------------------------------------------------

    [Fact]
    public void Options_DefaultsAreCorrect()
    {
        var options = new GraphRagOptions { IndexName = "my_index" };

        options.TopK.Should().Be(5);
        options.SearchMode.Should().Be(GraphRagSearchMode.Hybrid);
        options.FilterStopWords.Should().BeTrue();
        options.FulltextIndexName.Should().BeNull();
        options.RetrievalQuery.Should().BeNull();
    }
}
