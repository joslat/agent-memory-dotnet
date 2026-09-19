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

        if (Has("WITH $messages AS messages") &&
            Has("[msg IN $messages | msg.id] AS batchIds") &&
            Has("WITH DISTINCT msg.id AS id"))
        {
            return "MessageQueries.AddBatchOptimized";
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

        // E-1 IDENTITY EXPANSION. Method-built like the firing family, so it reaches here with no
        // constant to match -- and this file is where that was fixed for firing one commit ago, which
        // is the only reason it was noticed for this one. An unattributed query is invisible to the
        // per-scenario query counts the hermetic perf gate reads, and this hop adds a query per recall.
        //
        // The two-hop ABOUT pattern with the aliases guard is unique to it: nothing else in the
        // codebase traverses (:Fact)-[:ABOUT]->(:Entity)<-[:ABOUT]-(:Fact), and the node-distance
        // re-ranker -- the only other ABOUT consumer -- goes through shortestPath over a variable
        // length pattern instead.
        if (Has("(seed:Fact)-[:ABOUT]->(e:Entity)<-[:ABOUT]-(f:Fact)") &&
            Has("size(e.aliases) > 0"))
        {
            // The two overloads share the traversal and differ only in the CLOCKS, which is exactly
            // the pair a single fingerprint would collapse -- and collapsing them would report a
            // point-in-time read and a live one as the same query, hiding the distinction the
            // bitemporal twin exists to make.
            return Has("f.created_at <= datetime($systemAsOf)")
                ? "FactQueries.GetFactsSharingAliasedEntitiesAsOf"
                : "FactQueries.GetFactsSharingAliasedEntities";
        }

        // PROSPECTIVE FIRING -- all four shapes. Method-built like the delta family, so they arrive
        // here with no constant to match and, left unregistered, report as `unknown`. That costs the
        // per-scenario query attribution the hermetic perf gate reads, and the as-of pair is exactly
        // the path the next measurement wave turns on.
        //
        // The pairs separate on the CLOCK, which is the thing that actually differs between them.
        // Neither the RETURN clause nor the $since bound is used alone: delta recall shares both
        // (DeltaExpiredValidity orders by the same key, DeltaNewValidity bounds on the same $since).
        // What these four have and the delta family does not is $now / $validAsOf / $expiryHorizon.
        if (Has("MATCH (f:Fact)"))
        {
            var boundByTransactionClock = Has("f.created_at <= datetime($systemAsOf)");

            if (Has("f.valid_from IS NOT NULL") &&
                Has("RETURN f ORDER BY f.valid_from DESC LIMIT $limit"))
            {
                if (boundByTransactionClock && Has("f.valid_from <= datetime($validAsOf)"))
                    return "TemporalQueries.GetDueFactsAsOf";
                if (Has("f.valid_from <= datetime($now)"))
                    return "FactQueries.GetDueFacts";
            }

            if (Has("f.valid_until <= datetime($expiryHorizon)"))
            {
                if (boundByTransactionClock && Has("f.valid_until > datetime($validAsOf)"))
                    return "TemporalQueries.GetExpiringFactsAsOf";
                if (Has("f.valid_until > datetime($now)"))
                    return "FactQueries.GetExpiringFacts";
            }
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
