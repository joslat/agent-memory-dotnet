using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Abstractions.Services;

namespace Neo4j.AgentMemory.Core.Extraction.MergeStrategies;

/// <summary>
/// Factory that creates the correct merge strategy for a given <see cref="MergeStrategyType"/>.
/// </summary>
public static class MergeStrategyFactory
{
    /// <summary>
    /// Creates a merge strategy for <see cref="ExtractedEntity"/> items.
    /// </summary>
    public static IMergeStrategy<ExtractedEntity> CreateEntityStrategy(MergeStrategyType strategyType) =>
        strategyType switch
        {
            MergeStrategyType.Union => new UnionMergeStrategy<ExtractedEntity>(
                e => e.Name, e => e.Confidence),
            MergeStrategyType.Intersection => new IntersectionMergeStrategy<ExtractedEntity>(
                e => e.Name, e => e.Confidence),
            MergeStrategyType.Confidence => new ConfidenceMergeStrategy<ExtractedEntity>(
                e => e.Name, e => e.Confidence),
            MergeStrategyType.Cascade => new CascadeMergeStrategy<ExtractedEntity>(),
            MergeStrategyType.FirstSuccess => new FirstSuccessMergeStrategy<ExtractedEntity>(),
            _ => throw new ArgumentOutOfRangeException(nameof(strategyType), strategyType, "Unknown merge strategy type.")
        };

    /// <summary>
    /// Creates a merge strategy for <see cref="ExtractedFact"/> items.
    /// Key is the (subject, predicate, object) triple.
    /// </summary>
    public static IMergeStrategy<ExtractedFact> CreateFactStrategy(MergeStrategyType strategyType) =>
        strategyType switch
        {
            MergeStrategyType.Union => new UnionMergeStrategy<ExtractedFact>(
                FactKey, f => f.Confidence),
            MergeStrategyType.Intersection => new IntersectionMergeStrategy<ExtractedFact>(
                FactKey, f => f.Confidence),
            MergeStrategyType.Confidence => new ConfidenceMergeStrategy<ExtractedFact>(
                FactKey, f => f.Confidence),
            MergeStrategyType.Cascade => new CascadeMergeStrategy<ExtractedFact>(),
            MergeStrategyType.FirstSuccess => new FirstSuccessMergeStrategy<ExtractedFact>(),
            _ => throw new ArgumentOutOfRangeException(nameof(strategyType), strategyType, "Unknown merge strategy type.")
        };

    /// <summary>
    /// Creates a merge strategy for <see cref="ExtractedPreference"/> items.
    /// Key is the preference text.
    /// </summary>
    public static IMergeStrategy<ExtractedPreference> CreatePreferenceStrategy(MergeStrategyType strategyType) =>
        strategyType switch
        {
            MergeStrategyType.Union => new UnionMergeStrategy<ExtractedPreference>(
                p => p.PreferenceText, p => p.Confidence),
            MergeStrategyType.Intersection => new IntersectionMergeStrategy<ExtractedPreference>(
                p => p.PreferenceText, p => p.Confidence),
            MergeStrategyType.Confidence => new ConfidenceMergeStrategy<ExtractedPreference>(
                p => p.PreferenceText, p => p.Confidence),
            MergeStrategyType.Cascade => new CascadeMergeStrategy<ExtractedPreference>(),
            MergeStrategyType.FirstSuccess => new FirstSuccessMergeStrategy<ExtractedPreference>(),
            _ => throw new ArgumentOutOfRangeException(nameof(strategyType), strategyType, "Unknown merge strategy type.")
        };

    /// <summary>
    /// Creates a merge strategy for <see cref="ExtractedRelationship"/> items.
    /// Key is (sourceEntity, relationshipType, targetEntity).
    /// </summary>
    public static IMergeStrategy<ExtractedRelationship> CreateRelationshipStrategy(MergeStrategyType strategyType) =>
        strategyType switch
        {
            MergeStrategyType.Union => new UnionMergeStrategy<ExtractedRelationship>(
                RelationshipKey, r => r.Confidence),
            MergeStrategyType.Intersection => new IntersectionMergeStrategy<ExtractedRelationship>(
                RelationshipKey, r => r.Confidence),
            MergeStrategyType.Confidence => new ConfidenceMergeStrategy<ExtractedRelationship>(
                RelationshipKey, r => r.Confidence),
            MergeStrategyType.Cascade => new CascadeMergeStrategy<ExtractedRelationship>(),
            MergeStrategyType.FirstSuccess => new FirstSuccessMergeStrategy<ExtractedRelationship>(),
            _ => throw new ArgumentOutOfRangeException(nameof(strategyType), strategyType, "Unknown merge strategy type.")
        };

    private static string FactKey(ExtractedFact f) =>
        $"{f.Subject}|{f.Predicate}|{f.Object}".ToUpperInvariant();

    private static string RelationshipKey(ExtractedRelationship r) =>
        $"{r.SourceEntity}|{r.RelationshipType}|{r.TargetEntity}".ToUpperInvariant();
}
