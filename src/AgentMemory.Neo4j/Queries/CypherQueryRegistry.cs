using System.Reflection;

namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Discovers centralized Cypher constants once for validation and safe telemetry attribution.
/// </summary>
internal static class CypherQueryRegistry
{
    internal const string UnknownFingerprint = "unknown";

    private static readonly IReadOnlyDictionary<string, string> FingerprintsByCypher = GetAll()
        .GroupBy(query => query.Cypher, StringComparer.Ordinal)
        .ToDictionary(
            group => group.Key,
            group => group.Select(query => query.Name)
                .OrderBy(name => name, StringComparer.Ordinal)
                .First(),
            StringComparer.Ordinal);

    /// <summary>
    /// Returns a stable source identifier without ever returning the query text. Constants use the
    /// reflection-built map; centralized method-built queries use distinctive structural markers.
    /// Consumer-supplied or unrecognized text is intentionally <c>unknown</c>.
    /// </summary>
    internal static string FingerprintFor(string? cypher)
    {
        if (cypher is null)
            return UnknownFingerprint;
        if (FingerprintsByCypher.TryGetValue(cypher, out var fingerprint))
            return fingerprint;

        bool Has(string marker) => cypher.Contains(marker, StringComparison.Ordinal);

        if (Has("CREATE (:MemoryReadAudit"))
        {
            return Has("UNWIND $ids AS nodeId")
                ? "DecayQueries.UpdateAccessTimestampBatch"
                : "DecayQueries.UpdateAccessTimestamp";
        }

        var isMessageVectorSearch =
            Has("CALL db.index.vector.queryNodes('message_embedding_idx'") ||
            (Has("MATCH (:Conversation {session_id: $sessionId})-[:HAS_MESSAGE]->(node:Message)") &&
             Has("vector.similarity.cosine(node.embedding, $embedding)"));
        if (isMessageVectorSearch &&
            Has("RETURN node, score"))
        {
            return "MessageQueries.SearchByVector";
        }

        if (Has("CALL db.index.vector.queryNodes('entity_embedding_idx'"))
        {
            if (Has("node.created_at <= datetime($systemAsOf)"))
                return "TemporalQueries.SearchEntitiesAsOf";
            if (Has("MATCH (source:Entity") && Has("node.id <> $entityId"))
                return "EntityQueries.FindSimilarByEmbedding";
            if (Has("score >= $minScore"))
                return "EntityQueries.SearchByVector";
        }

        if (Has("MATCH (node:Fact)") &&
            Has("vector.similarity.cosine(node.embedding, $embedding)") &&
            Has("toLower(node.subject) = toLower($subject)") &&
            Has("toLower(node.predicate) = toLower($predicate)"))
            return "FactQueries.FindDuplicate";

        if (Has("CALL db.index.vector.queryNodes('fact_embedding_idx'"))
        {
            if (Has("node.created_at <= datetime($systemAsOf)"))
                return "TemporalQueries.SearchFactsAsOf";
            if (Has("toLower(node.subject) = toLower($subject)") &&
                Has("toLower(node.predicate) = toLower($predicate)"))
                return "FactQueries.FindDuplicate";
            if (Has("score >= $minScore"))
                return "FactQueries.SearchByVector";
        }

        if (Has("CALL db.index.vector.queryNodes('preference_embedding_idx'"))
        {
            if (Has("node.created_at <= datetime($systemAsOf)"))
                return "TemporalQueries.SearchPreferencesAsOf";
            if (Has("node.category = $category") && Has("score >= $threshold"))
                return "PreferenceQueries.FindDuplicate";
            if (Has("score >= $minScore"))
                return "PreferenceQueries.SearchByVector";
        }

        if (Has("CALL db.index.vector.queryNodes('task_embedding_idx'"))
        {
            return Has("node.started_at <= datetime($asOf)")
                ? "ReasoningQueries.SearchByTaskVectorAsOf"
                : "ReasoningQueries.SearchByTaskVector";
        }

        return UnknownFingerprint;
    }

    /// <summary>
    /// Returns all (name, cypherText) pairs from all *Queries classes in this assembly.
    /// </summary>
    public static IReadOnlyList<(string Name, string Cypher)> GetAll()
    {
        var queryTypes = typeof(CypherQueryRegistry).Assembly
            .GetTypes()
            .Where(t => !t.IsNested && t.IsAbstract && t.IsSealed // top-level static classes (public or internal)
                        && t.Name.EndsWith("Queries")
                        && t.Namespace == "AgentMemory.Neo4j.Queries")
            .OrderBy(t => t.FullName, StringComparer.Ordinal);

        var results = new List<(string, string)>();
        foreach (var type in queryTypes)
        {
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                         .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (field.IsLiteral && field.FieldType == typeof(string))
                {
                    var value = (string?)field.GetValue(null);
                    if (!string.IsNullOrWhiteSpace(value))
                        results.Add(($"{type.Name}.{field.Name}", value));
                }
            }
        }
        return results;
    }
}
