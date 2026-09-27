namespace AgentMemory.Core.Services;

/// <summary>
/// 36.2. The two similarity scales in the library, and the one conversion between them.
/// </summary>
/// <remarks>
/// <para>
/// A <b>store score</b> is what a vector search returns and what its <c>minScore</c> is compared with:
/// Neo4j's cosine vector index and <c>vector.similarity.cosine</c> both return <c>(1 + cos) / 2</c>, a
/// number in [0, 1]. <c>RecallOptions.MinSimilarityScore</c> = 0.7 is therefore a cosine of 0.40, and
/// 0.55 a cosine of 0.10, which admits almost everything (measured, H-8c).
/// </para>
/// <para>
/// A <b>cosine</b> is what the in-process matchers compute (<c>SemanticMatchThreshold</c>,
/// <c>WithinExtractionDuplicateThreshold</c>): a number in [-1, 1].
/// </para>
/// <para>
/// A threshold keeps the scale of the place it is compared, as documented on each option; where a cosine
/// threshold is handed to a store search it goes through <see cref="StoreScoreFromCosine"/>, here and
/// nowhere else.
/// </para>
/// </remarks>
internal static class SimilarityScale
{
    /// <summary>The store score that corresponds to <paramref name="cosine"/>.</summary>
    internal static double StoreScoreFromCosine(double cosine) => (1 + cosine) / 2;
}
