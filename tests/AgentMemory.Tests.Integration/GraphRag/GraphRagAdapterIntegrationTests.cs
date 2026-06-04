using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Integration.GraphRag;

/// <summary>
/// Spec §5.7 — GraphRAG adapter integration tests against live Neo4j (Testcontainers): provider
/// wiring, retrieval mode selection, result normalization, and blended context assembly. These
/// exercise the real <see cref="Neo4jGraphRagContextSource"/> retrievers (vector / fulltext / hybrid /
/// graph) over a small seeded <c>:Knowledge</c> graph with dedicated vector + fulltext indexes.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public class GraphRagAdapterIntegrationTests : IAsyncLifetime
{
    private const string VectorIndex = "knowledge_vec_idx";
    private const string FulltextIndex = "knowledge_ft_idx";

    // 4-dim unit vectors (matches the fixture's TestEmbeddingDimensions). AxisX nodes are the
    // intended vector matches; AxisY nodes are orthogonal distractors (cosine similarity 0).
    private static readonly float[] AxisX = [1f, 0f, 0f, 0f];
    private static readonly float[] AxisY = [0f, 1f, 0f, 0f];

    private readonly Neo4jIntegrationFixture _fixture;

    public GraphRagAdapterIntegrationTests(Neo4jIntegrationFixture fixture) => _fixture = fixture;

    public async Task InitializeAsync()
    {
        await _fixture.CleanDatabaseAsync();
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            $"CREATE VECTOR INDEX {VectorIndex} IF NOT EXISTS FOR (n:Knowledge) ON (n.embedding) " +
            "OPTIONS {indexConfig: {`vector.dimensions`: 4, `vector.similarity_function`: 'cosine'}}");
        await session.RunAsync(
            $"CREATE FULLTEXT INDEX {FulltextIndex} IF NOT EXISTS FOR (n:Knowledge) ON EACH [n.text]");
        await session.RunAsync("CALL db.awaitIndexes(60)");
    }

    public Task DisposeAsync() => Task.CompletedTask;

    // ── Retrieval mode selection + provider wiring ─────────────────────────────

    [Fact]
    public async Task VectorMode_ReturnsNearestNeighbour_OverDistractor()
    {
        await SeedAsync(
            ("k1", "graph databases store connected data", AxisX),
            ("k2", "relational databases store tables", AxisY));

        var source = CreateSource(GraphRagSearchMode.Vector, queryVector: AxisX);
        var result = await source.GetContextAsync(Request("graph"));

        result.Items.Should().NotBeEmpty();
        result.Items[0].Text.Should().Be("graph databases store connected data");
        result.Items[0].Score.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task FulltextMode_ReturnsKeywordMatch()
    {
        await SeedAsync(
            ("k1", "neo4j is a graph database", AxisX),
            ("k2", "postgres is a relational store", AxisY));

        var source = CreateSource(GraphRagSearchMode.Fulltext, queryVector: AxisX);
        var result = await source.GetContextAsync(Request("graph"));

        result.Items.Should().Contain(i => i.Text == "neo4j is a graph database");
        result.Items.Should().NotContain(i => i.Text == "postgres is a relational store");
    }

    [Fact]
    public async Task HybridMode_ReturnsMatch_FromCombinedSignals()
    {
        await SeedAsync(
            ("k1", "graph database knowledge", AxisX),
            ("k2", "entirely unrelated content", AxisY));

        var source = CreateSource(GraphRagSearchMode.Hybrid, queryVector: AxisX);
        var result = await source.GetContextAsync(Request("graph"));

        result.Items.Should().Contain(i => i.Text == "graph database knowledge");
    }

    [Fact]
    public async Task GraphMode_TraversesRelatedTo_AndReportsHops()
    {
        await SeedAsync(
            ("seed", "seed node", AxisX),
            ("neighbour", "neighbour node", AxisY));
        await LinkRelatedAsync("seed", "neighbour");

        var source = CreateSource(GraphRagSearchMode.Graph, queryVector: AxisX);
        var result = await source.GetContextAsync(Request("seed"));

        // GraphRetriever seeds by vector, then traverses RELATED_TO and joins "seed → related".
        result.Items.Should().Contain(i => i.Text == "seed node → neighbour node");
        var traversed = result.Items.First(i => i.Text.Contains('→'));
        traversed.Metadata.Should().ContainKey("hops");
    }

    // ── Result normalization (RetrieverResultItem → GraphRagContextItem) ────────

    [Fact]
    public async Task Normalization_PromotesScore_PreservesText_AndOmitsScoreFromMetadata()
    {
        await SeedAsync(("k1", "the only knowledge node", AxisX));

        var source = CreateSource(GraphRagSearchMode.Vector, queryVector: AxisX);
        var result = await source.GetContextAsync(Request("anything"));

        var item = result.Items.Single();
        item.Text.Should().Be("the only knowledge node");
        item.Score.Should().BeGreaterThan(0);          // promoted from the (node, score) record
        item.Metadata.Should().NotContainKey("score"); // promoted, not duplicated into metadata
    }

    // ── Blended context assembly (real GraphRAG source inside MemoryContextAssembler) ──

    [Fact]
    public async Task BlendedAssembly_IncludesGraphContext_AndMemoryOnlySuppressesIt()
    {
        await SeedAsync(("k1", "blended graph knowledge", AxisX));
        var graphRag = CreateSource(GraphRagSearchMode.Vector, queryVector: AxisX);
        var assembler = CreateAssemblerWithEmptyMemory(graphRag);

        var blended = await assembler.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s1",
            Query = "graph",
            QueryEmbedding = AxisX,
            Options = new RecallOptions { BlendMode = RetrievalBlendMode.Blended }
        });
        blended.GraphRagContext.Should().Contain("blended graph knowledge");

        var memoryOnly = await assembler.AssembleContextAsync(new RecallRequest
        {
            SessionId = "s1",
            Query = "graph",
            QueryEmbedding = AxisX,
            Options = new RecallOptions { BlendMode = RetrievalBlendMode.MemoryOnly }
        });
        memoryOnly.GraphRagContext.Should().BeNull();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static GraphRagContextRequest Request(string query, int topK = 5) =>
        new() { SessionId = "s1", Query = query, TopK = topK };

    private Neo4jGraphRagContextSource CreateSource(GraphRagSearchMode mode, float[] queryVector)
    {
        var options = Options.Create(new GraphRagOptions
        {
            IndexName = VectorIndex,
            FulltextIndexName = FulltextIndex,
            SearchMode = mode,
            TopK = 5,
            FilterStopWords = false,
            MaxTraversalHops = 2
        });
        return new Neo4jGraphRagContextSource(
            _fixture.Driver,
            new FixedVectorEmbeddingGenerator(queryVector),
            options,
            NullLogger<Neo4jGraphRagContextSource>.Instance);
    }

    private async Task SeedAsync(params (string Id, string Text, float[] Embedding)[] nodes)
    {
        await using var session = _fixture.Driver.AsyncSession();
        foreach (var n in nodes)
        {
            await session.RunAsync(
                "CREATE (k:Knowledge {id: $id, text: $text, embedding: $embedding})",
                new { id = n.Id, text = n.Text, embedding = n.Embedding.ToList() });
        }
    }

    private async Task LinkRelatedAsync(string fromId, string toId)
    {
        await using var session = _fixture.Driver.AsyncSession();
        await session.RunAsync(
            "MATCH (a:Knowledge {id: $from}), (b:Knowledge {id: $to}) MERGE (a)-[:RELATED_TO]->(b)",
            new { from = fromId, to = toId });
    }

    private MemoryContextAssembler CreateAssemblerWithEmptyMemory(IGraphRagContextSource graphRag)
    {
        var shortTerm = Substitute.For<IShortTermMemoryService>();
        shortTerm.GetRecentMessagesAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));
        shortTerm.SearchMessagesAsync(Arg.Any<string?>(), Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Message>>(Array.Empty<Message>()));

        var longTerm = Substitute.For<ILongTermMemoryService>();
        longTerm.SearchEntitiesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(Array.Empty<Entity>()));
        longTerm.SearchPreferencesAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Preference>>(Array.Empty<Preference>()));
        longTerm.SearchFactsAsync(Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Fact>>(Array.Empty<Fact>()));

        var reasoning = Substitute.For<IReasoningMemoryService>();
        reasoning.SearchSimilarTracesAsync(Arg.Any<float[]>(), Arg.Any<bool?>(), Arg.Any<int>(), Arg.Any<double>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ReasoningTrace>>(Array.Empty<ReasoningTrace>()));

        var embedding = Substitute.For<IEmbeddingOrchestrator>();
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var options = Options.Create(new MemoryOptions { EnableGraphRag = true });

        return new MemoryContextAssembler(
            shortTerm, longTerm, reasoning, graphRag, embedding, clock,
            options, NullLogger<MemoryContextAssembler>.Instance);
    }

    /// <summary>Deterministic embedding generator that returns a fixed query vector, so vector
    /// retrieval is reproducible without a real embedding provider.</summary>
    private sealed class FixedVectorEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly float[] _vector;
        public FixedVectorEmbeddingGenerator(float[] vector) => _vector = vector;

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values,
            EmbeddingGenerationOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var result = new GeneratedEmbeddings<Embedding<float>>();
            foreach (var _ in values)
                result.Add(new Embedding<float>(_vector.ToArray()));
            return Task.FromResult(result);
        }

        public object? GetService(Type serviceType, object? serviceKey = null)
            => serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose() { }
    }
}
