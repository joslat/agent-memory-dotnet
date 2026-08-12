using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Node-distance reranking (R6): boost what the query is <b>about</b>, not only what it looks like.
/// </summary>
/// <remarks>
/// <para>
/// The gates matter as much as the boost. This runs on the blocking recall path and issues two
/// queries per fact section, so every condition under which it declines to run is a cost avoided —
/// and a reranker that fires when it cannot change anything is pure overhead wearing a feature's name.
/// </para>
/// <para>
/// Shipped behind the retrieval-sufficiency work deliberately: reranking reorders survivors, so over a
/// starved candidate set it reorders seven of an owner's 504 facts and reports success. The signal
/// measured AUC 0.709–0.768 before this was built.
/// </para>
/// </remarks>
public sealed class NodeDistanceRerankerTests
{
    private readonly INeo4jTransactionRunner _tx = Substitute.For<INeo4jTransactionRunner>();

    private NodeDistanceReranker CreateSut(bool enabled = true, double gamma = 0.5) =>
        new(_tx,
            NullLogger<NodeDistanceReranker>.Instance,
            Options.Create(new MemoryRankingOptions { StructuralDecayGamma = gamma }),
            Options.Create(new MemoryOptions { NodeDistanceReranking = enabled }));

    private static MemoryRerankContext Context(
        MemoryItemKind kind = MemoryItemKind.Fact, float[]? embedding = null) =>
        new("who does alice work for", embedding ?? [0.1f, 0.2f], MemoryScope.For("alice"), kind);

    private static IReadOnlyList<MemoryContextRankedItem> Candidates() =>
    [
        new("fact-a", Score: 0.90, RetrievalRank: 1, ContextRank: 1),
        new("fact-b", Score: 0.80, RetrievalRank: 2, ContextRank: 2),
    ];

    // ── the gates ─────────────────────────────────────────────────────────

    [Fact]
    public void ItIsOffByDefault()
    {
        // It changes recall ordering and costs two queries per section; every recorded measurement was
        // taken without it.
        new MemoryOptions().NodeDistanceReranking.Should().BeFalse();
        CreateSut(enabled: false).IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void ItStaysOffWhenTheDecayConstantIsOne()
    {
        // At gamma = 1 every boost is 1.0, so the two queries could not change an ordering. Running
        // them anyway would be pure cost -- and it is the same constant GraphRAG hop-decay uses, so
        // "structural decay off" means off for both rather than for one of them.
        CreateSut(enabled: true, gamma: 1.0).IsEnabled.Should().BeFalse();
        CreateSut(enabled: true, gamma: 0.5).IsEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task ANonFactSectionIsLeftAlone()
    {
        // The distance query walks ABOUT/RELATED_TO from an entity to a Fact. Reordering traces or
        // preferences by a measure that does not apply to them would be worse than not running.
        var result = await CreateSut().RerankAsync(
            Candidates(), Context(kind: MemoryItemKind.Preference));

        result.Should().BeSameAs(await Task.FromResult(result));
        result.Select(r => r.ItemId).Should().Equal("fact-a", "fact-b");
        await _tx.DidNotReceive().ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AMissingQueryEmbeddingCostsNothing()
    {
        // A turn narrowed to categories needing no vector has no embedding, and the centroid lookup is
        // a vector search. Asking for one anyway would reintroduce exactly the round trip the trivial
        // turn policy exists to elide.
        var result = await CreateSut().RerankAsync(Candidates(), Context(embedding: []));

        result.Select(r => r.ItemId).Should().Equal("fact-a", "fact-b");
        await _tx.DidNotReceive().ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ASingleCandidateIsNotWorthTwoQueries()
    {
        // Nothing to reorder. The cheapest correct answer is the one already held.
        var single = new[] { new MemoryContextRankedItem("fact-a", 0.9, 1, 1) };

        var result = await CreateSut().RerankAsync(single, Context());

        result.Should().BeEquivalentTo(single);
        await _tx.DidNotReceive().ReadAsync(
            Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>());
    }

    // ── the boost ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ACloserCandidateOvertakesAHigherScoringDistantOne()
    {
        // The whole point. fact-b scores lower on similarity but sits one hop from the entity the
        // query names, while fact-a is four hops away: 0.80*(1+0.5^1)=1.20 beats 0.90*(1+0.5^4)=0.956.
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>())
            .Returns("entity-1");
        _tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyDictionary<string, int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["fact-a"] = 4,
                ["fact-b"] = 1,
            });

        var result = await CreateSut().RerankAsync(Candidates(), Context());

        result.Select(r => r.ItemId).Should().Equal("fact-b", "fact-a");
        result[0].ContextRank.Should().Be(1);
        result[1].ContextRank.Should().Be(2);
    }

    [Fact]
    public async Task ProximityDoesNotOverrideAStrongSimilarityGap()
    {
        // Graph closeness is evidence about relevance, not a replacement for it. The boost is
        // multiplicative so a candidate retrieval ranked badly is not promoted past a strong one by
        // adjacency alone: 0.90*(1+0.5^4)=0.956 still beats 0.20*(1+0.5)=0.30.
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>())
            .Returns("entity-1");
        _tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyDictionary<string, int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["fact-a"] = 4,
                ["weak"] = 1,
            });

        var candidates = new[]
        {
            new MemoryContextRankedItem("fact-a", 0.90, 1, 1),
            new MemoryContextRankedItem("weak", 0.20, 2, 2),
        };

        var result = await CreateSut().RerankAsync(candidates, Context());

        result.Select(r => r.ItemId).Should().Equal("fact-a", "weak");
    }

    [Fact]
    public async Task AnUnreachableCandidateKeepsItsScoreExactly()
    {
        // Absent from the distance result means "no path within the cap", which the caller reads as
        // no boost. A sentinel distance would invite arithmetic on a number meaning "unreachable".
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>())
            .Returns("entity-1");
        _tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyDictionary<string, int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["fact-b"] = 1,
            });

        var result = await CreateSut().RerankAsync(Candidates(), Context());

        // fact-b: 0.80*1.5 = 1.20; fact-a unreachable, keeps 0.90.
        result.Select(r => r.ItemId).Should().Equal("fact-b", "fact-a");
        result.Should().OnlyContain(r => r.Score == 0.90 || r.Score == 0.80,
            "the reranker reorders; it must not rewrite the provider's scores");
    }

    [Fact]
    public async Task NoCentroidMeansNoReordering()
    {
        // An owner whose graph holds no entity resembling the query. Returning the provider order is
        // correct and costs one query rather than two.
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>())
            .Returns((string?)null);

        var result = await CreateSut().RerankAsync(Candidates(), Context());

        result.Select(r => r.ItemId).Should().Equal("fact-a", "fact-b");
    }

    [Fact]
    public async Task NoCandidateWithinRangeLeavesTheOrderUntouched()
    {
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>())
            .Returns("entity-1");
        _tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyDictionary<string, int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal));

        var result = await CreateSut().RerankAsync(Candidates(), Context());

        result.Select(r => r.ItemId).Should().Equal("fact-a", "fact-b");
    }

    [Fact]
    public async Task TheCandidateSetIsNeverChanged()
    {
        // A reranker reorders. Adding or dropping a candidate would corrupt the section's diagnostics
        // silently, because Returned is counted before this runs.
        _tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<string?>>>(), Arg.Any<CancellationToken>())
            .Returns("entity-1");
        _tx.ReadAsync(
                Arg.Any<Func<IAsyncQueryRunner, Task<IReadOnlyDictionary<string, int>>>>(),
                Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<string, int>)new Dictionary<string, int>(StringComparer.Ordinal)
            {
                ["fact-b"] = 2,
            });

        var result = await CreateSut().RerankAsync(Candidates(), Context());

        result.Select(r => r.ItemId).Should().BeEquivalentTo(["fact-a", "fact-b"]);
        result.Should().HaveCount(2);
    }
}
