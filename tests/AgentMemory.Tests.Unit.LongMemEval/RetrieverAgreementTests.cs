using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The three-class retriever verdict, <b>read from the package rather than derived here</b>.
/// </summary>
/// <remarks>
/// <para>
/// This replaces a locally derived second-dense table, and it replaces it for cause. The derived
/// version applied a boolean AND — both retrievers clear the discrimination threshold ⇒ robust — and
/// therefore classified <b>six</b> shapes as robust that the publisher calls retriever-sensitive:
/// <c>arithmetic/delta</c>, <c>episodic/list-order</c>, <c>prospective/due-window</c>,
/// <c>temporal/recency</c>, <c>workingmemory/distance-8</c> and <c>distance-60</c>. The rule tested
/// whether both cleared the bar; it did not test whether the headroom MOVED between them, and on
/// those six it moves a long way.
/// </para>
/// <para>
/// The lesson is the publisher's own: two derivations of one number drift, and the drift is invisible
/// to both sides. These tests therefore assert that the field is READ, never that a rule reproduces it.
/// </para>
/// </remarks>
public sealed class RetrieverAgreementTests
{
    [Fact]
    public void EveryShippedVerticalDeclaresAVerdictForEveryShape()
    {
        var unknown = new List<string>();

        foreach (var descriptor in TypedMemEvalVerticals.All)
        {
            var shapes = TypedMemEvalRetrieverSensitivity.For(descriptor.Slug);
            shapes.Should().NotBeNull($"{descriptor.Slug} must declare retriever sensitivity");

            unknown.AddRange(shapes!
                .Where(entry => entry.Value.Agreement == DenseRankingClass.Unknown)
                .Select(entry => $"{descriptor.Slug}/{entry.Key}"));
        }

        unknown.Should().BeEmpty(
            "0.38 publishes retriever_agreement on every shape; an unreadable verdict means the "
            + "reader and the package have diverged, which is the drift co-publishing exists to stop");
    }

    /// <summary>All four classes are populated, so the read is not collapsing to one value.</summary>
    [Fact]
    public void TheFamilySpansAllFourClasses()
    {
        var byClass = TypedMemEvalVerticals.All
            .Select(v => TypedMemEvalRetrieverSensitivity.For(v.Slug))
            .Where(map => map is not null)
            .SelectMany(map => map!.Values)
            .GroupBy(shape => shape.Agreement)
            .ToDictionary(g => g.Key, g => g.Count());

        byClass.Should().ContainKeys(
            DenseRankingClass.Robust,
            DenseRankingClass.RetrieverSensitive,
            DenseRankingClass.NonRanking,
            DenseRankingClass.NotApplicable);
    }

    /// <summary>
    /// The six shapes a derived rule got wrong are read as SENSITIVE — pinned so the error cannot
    /// return by another route.
    /// </summary>
    [Theory]
    [InlineData("arithmetic", "delta")]
    [InlineData("episodic", "list-order")]
    [InlineData("prospective", "due-window")]
    [InlineData("temporal", "recency")]
    [InlineData("workingmemory", "distance-8")]
    [InlineData("workingmemory", "distance-60")]
    public void TheSixShapesADerivedRuleMisclassifiedAreSensitive(string vertical, string shape)
    {
        var shapes = TypedMemEvalRetrieverSensitivity.For(vertical);

        shapes.Should().NotBeNull();
        shapes![shape].Agreement.Should().Be(DenseRankingClass.RetrieverSensitive,
            "a boolean AND over two discrimination flags called this robust; the published verdict "
            + "accounts for how far the headroom moves between retrievers, and it moves a long way here");
    }

    /// <summary>
    /// An abstention shape publishes NO retrieval numbers at all, and is carried by its verdict alone.
    /// </summary>
    /// <remarks>
    /// Its gold set is empty, so <c>gold.issubset(top_k)</c> is vacuously true and ALLgold would read
    /// 1.000 under every retriever at every budget — the most flattering number in the block and the
    /// least true. The publisher withholds the fields deliberately; this asserts the reader carries
    /// the shape anyway rather than dropping it, and never invents a headroom for it.
    /// </remarks>
    [Fact]
    public void TheAbstentionShapeIsCarriedByItsVerdictWithNoRetrievalNumbers()
    {
        var forgetting = TypedMemEvalRetrieverSensitivity.For("forgetting");

        forgetting.Should().NotBeNull();
        forgetting!.Should().ContainKey("never-known");

        var neverKnown = forgetting["never-known"];
        neverKnown.Agreement.Should().Be(DenseRankingClass.NotApplicable);
        neverKnown.HeadroomDense.Should().BeNull("no headroom is published, so none may be assumed");
        neverKnown.SecondDenseHeadroom.Should().BeNull();
    }

    /// <summary>The second dense column is read where it is published.</summary>
    [Fact]
    public void TheSecondDenseHeadroomIsRead()
    {
        var measured = TypedMemEvalVerticals.All
            .Select(v => TypedMemEvalRetrieverSensitivity.For(v.Slug))
            .Where(map => map is not null)
            .SelectMany(map => map!.Values)
            .Where(shape => shape.Agreement != DenseRankingClass.NotApplicable)
            .ToArray();

        measured.Should().NotBeEmpty();
        measured.Should().OnlyContain(shape => shape.SecondDenseHeadroom.HasValue,
            "0.38 publishes second_dense on every measured shape; a missing value means the reader "
            + "is looking in the wrong place");
    }
}
