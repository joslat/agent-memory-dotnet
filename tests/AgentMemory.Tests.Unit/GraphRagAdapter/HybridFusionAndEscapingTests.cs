using FluentAssertions;
using AgentMemory.Neo4j.Retrieval;
using AgentMemory.Neo4j.Retrieval.Internal;

namespace AgentMemory.Tests.Unit.GraphRagAdapter;

/// <summary>
/// cycle-5: GraphRAG retrieval-correctness fixes — Reciprocal Rank Fusion for hybrid blending (#4) and
/// Lucene metacharacter escaping for the raw fulltext-query path (#2).
/// </summary>
public sealed class HybridFusionAndEscapingTests
{
    // ── RRF fusion (#4) ──────────────────────────────────────────────────────

    [Fact]
    public void FuseReciprocalRank_ItemInBothLists_OutranksSingleListItems()
    {
        // RRF compares by RANK, not by raw vector([0,1]) vs BM25(unbounded) scores. An item present in
        // both ranked lists is reinforced and rises above items present in only one.
        var vector = new List<RetrieverResultItem>
        {
            new("A"), new("B"), new("C"),
        };
        var fulltext = new List<RetrieverResultItem>
        {
            new("C"), new("D"), new("A"),
        };

        var fused = HybridRetriever.FuseReciprocalRank(vector, fulltext, topK: 10);

        fused.Select(i => i.Content).Should().OnlyHaveUniqueItems("duplicate content must be deduped");
        fused.Take(2).Select(i => i.Content).Should().BeEquivalentTo(new[] { "A", "C" },
            "A and C appear in both lists, so RRF ranks them above the single-list B and D");
        fused.Select(i => i.Content).Should().Contain(new[] { "B", "D" });
    }

    [Fact]
    public void FuseReciprocalRank_RespectsTopK()
    {
        var vector = new List<RetrieverResultItem> { new("A"), new("B") };
        var fulltext = new List<RetrieverResultItem> { new("C"), new("D") };

        HybridRetriever.FuseReciprocalRank(vector, fulltext, topK: 2).Should().HaveCount(2);
    }

    [Fact]
    public void FuseReciprocalRank_HigherRankInOwnList_ScoresHigher()
    {
        // Within a single list, an earlier (higher-ranked) item must end up ahead of a later one.
        var vector = new List<RetrieverResultItem> { new("first"), new("second"), new("third") };
        var fulltext = new List<RetrieverResultItem>();

        var fused = HybridRetriever.FuseReciprocalRank(vector, fulltext, topK: 10);

        fused.Select(i => i.Content).Should().ContainInOrder("first", "second", "third");
    }

    // ── Lucene escaping (#2) ─────────────────────────────────────────────────

    [Theory]
    [InlineData("C++ vs Rust: faster?")]
    [InlineData("(a OR b)")]
    [InlineData("-money \"quote [x] {y} ~z *w ?q ^h /p \\b &c |d !e")]
    public void Escape_EveryMetacharacterIsBackslashEscaped(string input)
    {
        var escaped = LuceneQueryEscaper.Escape(input);
        // Every Lucene metacharacter in the output must be immediately preceded by an (escaping) backslash.
        const string special = "+-!(){}[]^\"~*?:/&|";
        for (int i = 0; i < escaped.Length; i++)
        {
            if (special.IndexOf(escaped[i]) >= 0)
                (i > 0 && escaped[i - 1] == '\\').Should().BeTrue($"char '{escaped[i]}' at {i} must be escaped");
        }
    }

    [Fact]
    public void Escape_PlainText_IsUnchanged()
    {
        LuceneQueryEscaper.Escape("hello world").Should().Be("hello world");
    }

    [Fact]
    public void Escape_NullOrEmpty_ReturnsEmpty()
    {
        LuceneQueryEscaper.Escape(null).Should().BeEmpty();
        LuceneQueryEscaper.Escape("").Should().BeEmpty();
    }

    [Fact]
    public void Escape_EscapesEachSpecialChar()
    {
        LuceneQueryEscaper.Escape("a+b").Should().Be(@"a\+b");
        LuceneQueryEscaper.Escape("a:b").Should().Be(@"a\:b");
        LuceneQueryEscaper.Escape("(x)").Should().Be(@"\(x\)");
    }
}
