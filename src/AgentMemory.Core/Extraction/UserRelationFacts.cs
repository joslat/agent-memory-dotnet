using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Extraction;

/// <summary>
/// USER-RELATION-DROP: a relationship between the owner ("user", "I", "me") and someone else, as the fact recall's
/// connections follow when there is no named person of the owner's to anchor an edge on.
/// </summary>
internal static class UserRelationFacts
{
    /// <summary>Whether exactly one end is the owner and the other end names someone.</summary>
    internal static bool ToUser(ExtractedRelationship relationship)
    {
        var source = MemoryUserNames.MeansUserEndpoint(relationship.SourceEntity, source: true);
        var target = MemoryUserNames.MeansUserEndpoint(relationship.TargetEntity, source: false);
        if (source == target) return false;
        return !string.IsNullOrWhiteSpace(source ? relationship.TargetEntity : relationship.SourceEntity);
    }

    /// <summary>"Theo -[BROTHER_OF]-> user" as "Theo | brother of | user"; "user -[WORKS_AT]-> Acme" as "user | works at | Acme".</summary>
    internal static ExtractedFact AsFact(ExtractedRelationship relationship)
    {
        var ownerIsSource = MemoryUserNames.MeansUserEndpoint(relationship.SourceEntity, source: true);
        return new ExtractedFact
        {
            Subject = ownerIsSource ? "user" : relationship.SourceEntity.Trim(),
            Predicate = Phrase(relationship.RelationshipType),
            Object = ownerIsSource ? relationship.TargetEntity.Trim() : "user",
            Confidence = relationship.Confidence,
        };
    }

    /// <summary>A relationship type in the words a fact uses: "BROTHER_OF" reads "brother of".</summary>
    internal static string Phrase(string? relationshipType)
    {
        var words = (relationshipType ?? "").Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return words.Length == 0 ? "related to" : string.Join(' ', words).ToLowerInvariant();
    }
}
