using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The second dense retriever, and the three-class read the 2026-09-15 ruling requires.
/// </summary>
/// <remarks>
/// <para>
/// The sidecar's <c>discriminates_under_dense</c> is measured with <c>text-embedding-ada-002</c> — a
/// 2022 model the sidecar did not name until 2026-09-14. Against <c>text-embedding-3-small</c> the
/// flag <b>flips on 4 of 35 shapes</b>, and <b>neither embedder is ours</b>. So the boolean demotes
/// from a filter to three classes: ROBUST-RANKING, RETRIEVER-SENSITIVE, NON-RANKING.
/// </para>
/// <para>
/// <b>The second column is DERIVED, not published</b>, so these tests exist to keep the derivation
/// honest. Two of them are load-bearing: that the tool's ada column reproduces the package's own
/// published headroom, which is what licenses using its second column at all; and that the inferred
/// threshold reproduces the published boolean on every shape, so an upstream rule change fails here
/// rather than silently reclassifying a cell.
/// </para>
/// </remarks>
public sealed class DenseSecondOpinionTests
{
    /// <summary>
    /// THE LICENCE: the compare tool's ada column IS the package's published column.
    /// </summary>
    /// <remarks>
    /// If these disagreed, the tool would be measuring something other than what the sidecar
    /// publishes, and its second column would describe a retrieval this project never reads. They
    /// agree on all 35 shapes, which is the only reason the 3-small numbers are usable.
    /// </remarks>
    [Fact]
    public void TheDerivedAdaColumnReproducesThePackagesPublishedHeadroom()
    {
        var mismatches = new List<string>();

        foreach (var descriptor in TypedMemEvalVerticals.All)
        {
            if (TypedMemEvalRetrieverSensitivity.For(descriptor.Slug) is not { } shapes) continue;

            foreach (var (shape, published) in shapes)
            {
                var derived = TypedMemEvalDenseSecondOpinion.For(descriptor.Slug, shape);
                if (derived is null || published.HeadroomDense is not { } publishedHeadroom) continue;

                if (Math.Abs(publishedHeadroom - derived.Value.AdaHeadroom) > 0.0015)
                {
                    mismatches.Add(
                        $"{descriptor.Slug}/{shape}: package {publishedHeadroom:F3} vs derived "
                        + $"{derived.Value.AdaHeadroom:F3}");
                }
            }
        }

        mismatches.Should().BeEmpty(
            "the compare tool's ada arm must reproduce the published column exactly, or its second "
            + "column is describing a different retrieval from the one the sidecar reports");
    }

    /// <summary>
    /// THE INFERENCE, pinned: the threshold reproduces the published boolean on every shape.
    /// </summary>
    /// <remarks>
    /// The sidecar ships the boolean and the headroom but not the rule joining them, so the rule is
    /// inferred. It separates cleanly — every FALSE shape sits at or below 0.100 and every TRUE shape
    /// at or above 0.167 — but an inference that silently stopped holding would reclassify the second
    /// column with nothing to show for it. This fails instead.
    /// </remarks>
    [Fact]
    public void TheInferredThresholdReproducesEveryPublishedBoolean()
    {
        var wrong = new List<string>();
        var checkedShapes = 0;

        foreach (var descriptor in TypedMemEvalVerticals.All)
        {
            if (TypedMemEvalRetrieverSensitivity.For(descriptor.Slug) is not { } shapes) continue;

            foreach (var (shape, published) in shapes)
            {
                if (published.HeadroomDense is not { } headroom) continue;

                checkedShapes++;
                var derived = headroom > TypedMemEvalDenseSecondOpinion.DiscriminationThreshold + 1e-9;
                if (derived != published.Discriminates)
                {
                    wrong.Add($"{descriptor.Slug}/{shape}: headroom {headroom:F3}, "
                        + $"published {published.Discriminates}, derived {derived}");
                }
            }
        }

        checkedShapes.Should().Be(35, "the family declares 35 shapes with a dense headroom");
        wrong.Should().BeEmpty(
            "the threshold is INFERRED from the published pairs; if it ever stops reproducing them, "
            + "the second column's classification is no longer trustworthy and must not pass quietly");
    }

    /// <summary>Every shape the package classifies has a second opinion.</summary>
    [Fact]
    public void TheSecondOpinionCoversEveryClassifiedShape()
    {
        var missing = new List<string>();

        foreach (var descriptor in TypedMemEvalVerticals.All)
        {
            if (TypedMemEvalRetrieverSensitivity.For(descriptor.Slug) is not { } shapes) continue;

            missing.AddRange(shapes.Keys
                .Where(shape => TypedMemEvalDenseSecondOpinion.For(descriptor.Slug, shape) is null)
                .Select(shape => $"{descriptor.Slug}/{shape}"));
        }

        missing.Should().BeEmpty(
            "a shape with only one retriever's opinion cannot be called robust, and falling back "
            + "silently would rebuild the single-retriever filter this replaces");
    }

    /// <summary>
    /// The population is genuinely three classes, and the sensitive count is the ruled one.
    /// </summary>
    /// <remarks>
    /// The coordinator's ruling states the flag flips on 4 of 35 shapes. That number was derived
    /// independently here from the tool's output; asserting it pins the headline the whole
    /// three-class read rests on.
    /// </remarks>
    [Fact]
    public void ExactlyFourShapesFlipBetweenTheTwoRetrievers()
    {
        var byClass = TypedMemEvalDenseSecondOpinion.All.Values
            .GroupBy(pair => pair.Class)
            .ToDictionary(group => group.Key, group => group.Count());

        TypedMemEvalDenseSecondOpinion.All.Should().HaveCount(35);
        byClass.GetValueOrDefault(DenseRankingClass.RetrieverSensitive).Should().Be(4,
            "the ruling names four flips, and a different count means the derivation moved");
        byClass.GetValueOrDefault(DenseRankingClass.Robust).Should().BeGreaterThan(0);
        byClass.GetValueOrDefault(DenseRankingClass.NonRanking).Should().BeGreaterThan(0);
    }

    /// <summary>The four flips are the named ones, in both directions.</summary>
    /// <remarks>
    /// One of the four flips the OTHER way — <c>workingmemory/distance-40</c> is non-ranking under
    /// ada and ranking under 3-small. A sensitive class that only ever meant "loses its flag" would
    /// have missed it, and with it the one shape the newer retriever rescues.
    /// </remarks>
    [Fact]
    public void TheFlipsRunInBothDirections()
    {
        var sensitive = TypedMemEvalDenseSecondOpinion.All
            .Where(entry => entry.Value.Class == DenseRankingClass.RetrieverSensitive)
            .ToDictionary(entry => entry.Key, entry => entry.Value);

        sensitive.Keys.Should().BeEquivalentTo(
        [
            "forgetting/still-valid",
            "temporal/interval-position",
            "workingmemory/distance-25",
            "workingmemory/distance-40",
        ]);

        sensitive["workingmemory/distance-40"].DiscriminatesUnderAda.Should().BeFalse();
        sensitive["workingmemory/distance-40"].DiscriminatesUnderSmall.Should().BeTrue(
            "the newer retriever RESCUES this shape, so 'sensitive' cannot mean 'demoted'");
    }

    /// <summary>A shape ranking under both retrievers is robust; one is never enough.</summary>
    [Fact]
    public void RobustRequiresBothRetrievers()
    {
        new DenseHeadroomPair(0.50, 0.50).Class.Should().Be(DenseRankingClass.Robust);
        new DenseHeadroomPair(0.50, 0.00).Class.Should().Be(DenseRankingClass.RetrieverSensitive);
        new DenseHeadroomPair(0.00, 0.50).Class.Should().Be(DenseRankingClass.RetrieverSensitive);
        new DenseHeadroomPair(0.00, 0.00).Class.Should().Be(DenseRankingClass.NonRanking);
    }
}
