using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// Which shapes can rank systems FOR US — every published headroom figure is conditioned on BM25,
/// and this stack retrieves with embeddings.
/// </summary>
/// <remarks>
/// A shape with <c>discriminates_under_dense: false</c> can still rank two systems for a BM25
/// consumer and cannot for an embedding one. Placing such a shape on our scoreboard would rank
/// systems on a measurement that cannot separate them — worse than reporting nothing, because the
/// number looks like evidence.
/// </remarks>
public sealed class RetrieverSensitivityTests
{
    [Fact]
    public void EveryShippedVerticalDeclaresSensitivity()
    {
        var missing = TypedMemEvalVerticals.All
            .Select(descriptor => descriptor.Slug)
            .Where(slug => TypedMemEvalRetrieverSensitivity.For(slug) is null)
            .ToArray();

        missing.Should().BeEmpty(
            "0.36 ships probes.retriever_sensitivity on every corpus, and a vertical without it "
            + "cannot be read through the third column the C-D prereg requires");
    }

    /// <summary>
    /// The family really does contain non-ranking shapes — so the flag is not uniformly true.
    /// </summary>
    /// <remarks>
    /// A reader that returned "discriminates" for everything would pass a smoke test and hide the
    /// whole point. The coordinator's count is 8 of 35 at K=5; this asserts the population is mixed
    /// rather than pinning their exact number, which is theirs to revise.
    /// </remarks>
    [Fact]
    public void TheFamilyContainsBothRankingAndNonRankingShapes()
    {
        var all = TypedMemEvalVerticals.All
            .Select(descriptor => TypedMemEvalRetrieverSensitivity.For(descriptor.Slug))
            .Where(map => map is not null)
            .SelectMany(map => map!.Values)
            .ToArray();

        all.Should().NotBeEmpty();
        all.Should().Contain(shape => shape.Discriminates,
            "some shapes do rank under a dense retriever");
        all.Should().Contain(shape => !shape.Discriminates,
            "and some do not — if this flag were uniformly true the column would be decoration");
    }

    /// <summary>An unknown vertical is null, never silently rankable.</summary>
    [Fact]
    public void AnUnknownVerticalIsNotSilentlyRankable() =>
        TypedMemEvalRetrieverSensitivity.For("not-a-vertical").Should().BeNull();

    /// <summary>Both headroom conditions are carried, because they differ and only one is ours.</summary>
    [Fact]
    public void BothHeadroomConditionsAreRead()
    {
        var conjunction = TypedMemEvalRetrieverSensitivity.For("conjunction");

        conjunction.Should().NotBeNull();
        conjunction!.Values.Should().OnlyContain(
            shape => shape.HeadroomDense.HasValue && shape.HeadroomBm25.HasValue,
            "the BM25 figure is the condition every published number carries, and the dense figure "
            + "is the one that applies to this stack");
    }
}
