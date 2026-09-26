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

        // THE OWNER-SCOPED SCANS: the last-resort fallback of owner-scoped vector search (the index is
        // global, so another tenant's rows can crowd an owner out of top-K entirely; this scores the
        // owner's own rows directly). Method-built, so they reached the structural fallback: a traced
        // session found them on 27% of recall legs as four anonymous hashes.
        if (Has("vector.similarity.cosine(") && Has("$embedding") && !Has("db.index.vector.queryNodes"))
        {
            var asOf = Has("$systemAsOf") || Has("$asOf") ? "AsOf" : string.Empty;
            if (Has("MATCH (n:Entity)")) return "EntityQueries.OwnerScopedScan" + asOf;
            if (Has("MATCH (n:Preference)")) return "PreferenceQueries.OwnerScopedScan" + asOf;
            if (Has("MATCH (f:Fact)")) return "FactQueries.OwnerScopedScan" + asOf;
            if (Has("MATCH (t:ReasoningTrace)")) return "ReasoningQueries.OwnerScopedScan" + asOf;
        }

        // Entity resolution's candidate set: every live entity of one type for the owner (with vectors,
        // or without them when the semantic stage goes through the index).
        if (Has("MATCH (e:Entity {type: $type})") && Has("RETURN e {"))
            return "EntityQueries.GetByTypeWithoutEmbedding";
        if (Has("MATCH (e:Entity {type: $type})") && Has("RETURN e"))
            return "EntityQueries.GetByType";

        // Fact dedup-on-create by its merge key (served by fact_merge_key_idx).
        if (Has("f.subject_key = $subjectKey") && Has("f.owner_key = $ownerKey") && Has("RETURN f LIMIT 1"))
            return "FactQueries.FindByMergeKey";

        // Schema bootstrap: the vector indexes are built with the configured dimension, so never constant.
        if (Has("CREATE VECTOR INDEX"))
            return "SchemaQueries.CreateVectorIndex";

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

        return ConsumerQuery.Value ? UnknownFingerprint : StructuralFingerprint(cypher) ?? UnknownFingerprint;
    }

    private static readonly AsyncLocal<bool> ConsumerQuery = new();

    /// <summary>
    /// Marks the queries run inside the scope as caller-supplied Cypher (the graph-query service): they are
    /// never given a structural name, even when they mention AgentMemory's labels, so their text (and any
    /// literal in it) cannot become a telemetry dimension.
    /// </summary>
    internal static IDisposable ConsumerQueries()
    {
        var previous = ConsumerQuery.Value;
        ConsumerQuery.Value = true;
        return new Restore(previous);
    }

    private sealed class Restore(bool previous) : IDisposable
    {
        public void Dispose() => ConsumerQuery.Value = previous;
    }

    // AgentMemory's own vector indexes and node labels. A query naming one of them is ours even when no
    // marker above recognises this exact variant (a method-built text with an inlined top-K, a schema
    // statement, a candidate read), and deserves a stable name rather than joining one "unknown" bucket
    // that hid 340 of a session's queries. Consumer-supplied text names none of them and stays unknown.
    private static readonly string[] OwnIndexes =
    [
        "message_embedding_idx", "entity_embedding_idx", "fact_embedding_idx", "preference_embedding_idx",
        "reasoning_step_embedding_idx", "task_embedding_idx",
    ];

    private static readonly string[] OwnLabels =
    [
        "Message", "Conversation", "Entity", "Fact", "Preference", "ReasoningTrace", "ReasoningStep", "ToolCall",
        "MemoryReadAudit", "ConsolidationRun",
    ];

    // Precomputed once: the fallback runs on every unrecognised query while traced.
    private static readonly (string Name, string Quoted, string Created)[] OwnIndexNeedles =
        OwnIndexes.Select(index => (index, $"'{index}'", $"INDEX {index} ")).ToArray();

    private static readonly (string Name, string[] Forms)[] OwnLabelNeedles =
        OwnLabels.Select(label => (label, new[] { $":{label} ", $":{label})", $":{label} {{" })).ToArray();

    /// <summary>
    /// <c>unregistered:&lt;index-or-label&gt;:&lt;6 hex&gt;</c> for AgentMemory's own queries that no marker
    /// recognises, else null. The hash covers the text with numbers normalised, so top-K variants of one
    /// query share a name. Never the text itself.
    /// </summary>
    internal static string? StructuralFingerprint(string cypher)
    {
        string? anchor = null;
        for (var i = 0; i < OwnIndexNeedles.Length && anchor is null; i++)
            if (cypher.Contains(OwnIndexNeedles[i].Quoted, StringComparison.Ordinal) ||
                cypher.Contains(OwnIndexNeedles[i].Created, StringComparison.Ordinal))
                anchor = OwnIndexNeedles[i].Name;
        for (var i = 0; i < OwnLabelNeedles.Length && anchor is null; i++)
            if (OwnLabelNeedles[i].Forms.Any(form => cypher.Contains(form, StringComparison.Ordinal)))
                anchor = OwnLabelNeedles[i].Name;
        if (anchor is null) return null;
        // Literals and numbers normalised, so a name is a query SHAPE: bounded cardinality, no values.
        var normalised = System.Text.RegularExpressions.Regex.Replace(
            cypher, @"'(?:[^'\\]|\\.)*'|""(?:[^""\\]|\\.)*""|[0-9]+|\s+",
            m => char.IsWhiteSpace(m.Value[0]) ? " " : m.Value[0] is '\'' or '"' ? "?" : "#");
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(normalised));
        return $"unregistered:{anchor}:{Convert.ToHexString(hash, 0, 3).ToLowerInvariant()}";
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
