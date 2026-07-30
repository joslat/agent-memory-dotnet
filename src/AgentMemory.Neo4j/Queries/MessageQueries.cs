using AgentMemory.Neo4j.Infrastructure;

namespace AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Message operations.
/// Each constant corresponds to exactly one repository method in
/// <see cref="AgentMemory.Neo4j.Repositories.Neo4jMessageRepository"/>.
/// </summary>
internal static class MessageQueries
{
    // ── AddAsync ───────────────────────────────────────────────────────

    /// <summary>Create a message, persist its optional embedding, and maintain its conversation/order
    /// links in one query. The conversation
    /// is MERGE-d so persisting a message never silently no-ops when the conversation was not
    /// explicitly created first (e.g. from the MAF context/history providers); a thin conversation
    /// is created and later enriched by ConversationQueries.Upsert.
    /// The message node itself is also MERGE-d by id (ON CREATE SET only, first-write-wins) rather than
    /// CREATE-d, so callers that supply a deterministic id (see IMemoryIngestion.AddMessageWithIdAsync)
    /// can safely persist "the same" message more than once -- a second call with the same id is a no-op
    /// for content and never creates a duplicate node or a duplicate HAS_MESSAGE edge. Safe because
    /// message_id has a uniqueness constraint (SchemaQueries), so MERGE uses the backing index. Every
    /// existing caller is unaffected: nothing before this ever supplied a repeated id (fresh GUID each
    /// call), so this is purely additive infrastructure.</summary>
    public const string Add = @"
            MERGE (conv:Conversation {id: $conversationId})
            ON CREATE SET conv.session_id = $sessionId,
                          conv.created_at = datetime($timestamp),
                          conv.updated_at = datetime($timestamp)
            MERGE (m:Message {id: $id})
            ON CREATE SET
                m.conversation_id = $conversationId,
                m.session_id      = $sessionId,
                m.role            = $role,
                m.content         = $content,
                m.timestamp       = datetime($timestamp),
                m.tool_call_ids   = $toolCallIds,
                m.metadata        = $metadata
            WITH conv, m, m { .* } AS persisted
            SET m.embedding = CASE
                WHEN $embedding IS NOT NULL THEN $embedding
                ELSE m.embedding
            END
            MERGE (conv)-[:HAS_MESSAGE]->(m)
            WITH conv, m, persisted
            OPTIONAL MATCH (conv)-[:FIRST_MESSAGE]->(first:Message)
            FOREACH (_ IN CASE WHEN first IS NULL THEN [1] ELSE [] END |
                MERGE (conv)-[:FIRST_MESSAGE]->(m)
            )
            WITH conv, m, persisted
            OPTIONAL MATCH (conv)-[:HAS_MESSAGE]->(prev:Message)
            WHERE prev.id <> $id
            WITH m, persisted, prev ORDER BY prev.timestamp DESC
            WITH m, persisted, head(collect(prev)) AS prev
            FOREACH (_ IN CASE WHEN prev IS NULL THEN [] ELSE [1] END |
                MERGE (prev)-[:NEXT_MESSAGE]->(m)
            )
            RETURN persisted AS m";

    /// <summary>Link the first message in a conversation via FIRST_MESSAGE.</summary>
    public const string CreateFirstMessageLink = @"
                MATCH (conv:Conversation {id: $conversationId}), (m:Message {id: $id})
                WHERE NOT EXISTS { MATCH (conv)-[:FIRST_MESSAGE]->() }
                MERGE (conv)-[:FIRST_MESSAGE]->(m)";

    /// <summary>Establish NEXT_MESSAGE linked-list pointer from previous last message. MERGE (not CREATE)
    /// so re-persisting a message with the same id never creates a second edge to the SAME prev/next pair.
    /// NOTE: "prev" is re-selected fresh on every call (not carried from a prior call). This DOES happen in
    /// the real cross-provider-in-one-turn scenario (#89): Neo4jChatHistoryProvider persists a request
    /// message between the two providers' response-persist calls, so the second response-persist links a
    /// NEW prev->response edge, forming an inert 2-cycle rather than a duplicate of the first edge. Harmless
    /// because NEXT_MESSAGE (like FIRST_MESSAGE) is write-only -- nothing in this codebase reads or
    /// traverses either relationship; ordering is always read via the `timestamp` property. Not guarded
    /// against since there's nothing depending on it being a clean linked list.</summary>
    public const string LinkNextMessage = @"
            MATCH (conv:Conversation {id: $conversationId})-[:HAS_MESSAGE]->(prev:Message)
            WHERE prev.id <> $id
            WITH prev ORDER BY prev.timestamp DESC LIMIT 1
            MATCH (m:Message {id: $id})
            MERGE (prev)-[:NEXT_MESSAGE]->(m)";

    // ── AddBatchAsync ──────────────────────────────────────────────────

    /// <summary>Batch create messages and link to conversations via UNWIND. MERGE-d by id (ON CREATE SET
    /// only) for the same idempotency guarantee as <see cref="Add"/>; safe under UNWIND because message_id
    /// is a uniqueness-constrained property (no eagerness-analysis duplicate-row hazard). NOTE: this API
    /// is only ever fed fresh-GUID messages today (batch ingestion doesn't accept caller-supplied ids) --
    /// if a caller ever passed two Message objects with the same id in one call, MERGE would collapse them
    /// to one node and the returned list would have one fewer entry than the input, silently. Out of scope
    /// to guard against since nothing does this.</summary>
    public const string AddBatch = @"
            UNWIND $messages AS msg
            MERGE (conv:Conversation {id: msg.conversation_id})
            ON CREATE SET conv.session_id = msg.session_id,
                          conv.created_at = datetime(msg.timestamp),
                          conv.updated_at = datetime(msg.timestamp)
            MERGE (m:Message {id: msg.id})
            ON CREATE SET
                m.conversation_id = msg.conversation_id,
                m.session_id      = msg.session_id,
                m.role            = msg.role,
                m.content         = msg.content,
                m.timestamp       = datetime(msg.timestamp),
                m.tool_call_ids   = msg.tool_call_ids,
                m.metadata        = msg.metadata
            MERGE (conv)-[:HAS_MESSAGE]->(m)
            RETURN m";

    /// <summary>
    /// One-query batch write preserving <see cref="AddBatch"/>'s behavior: first-write-wins message
    /// properties, unconditional overwrite for supplied embeddings, intra-batch ordering, connection to
    /// the prior conversation tail, and ordered read-back. The input must already be timestamp ordered.
    /// </summary>
    public static string AddBatchOptimized { get; } = @"
            WITH $messages AS messages,
                 [msg IN $messages | msg.id] AS batchIds
            UNWIND messages AS msg
            MERGE (conv:Conversation {id: msg.conversation_id})
            ON CREATE SET conv.session_id = msg.session_id,
                          conv.created_at = datetime(msg.timestamp),
                          conv.updated_at = datetime(msg.timestamp)
            MERGE (m:Message {id: msg.id})
            ON CREATE SET
                m.conversation_id = msg.conversation_id,
                m.session_id      = msg.session_id,
                m.role            = msg.role,
                m.content         = msg.content,
                m.timestamp       = datetime(msg.timestamp),
                m.tool_call_ids   = msg.tool_call_ids,
                m.metadata        = msg.metadata
            FOREACH (_ IN CASE WHEN msg.embedding IS NULL THEN [] ELSE [1] END |
                SET m.embedding = msg.embedding
            )
            MERGE (conv)-[:HAS_MESSAGE]->(m)
            WITH messages, batchIds
            CALL {
                WITH messages
                UNWIND CASE
                    WHEN size(messages) > 1 THEN range(1, size(messages) - 1)
                    ELSE []
                END AS i
                MATCH (prev:Message {id: messages[i - 1].id})
                MATCH (next:Message {id: messages[i].id})
                MERGE (prev)-[:NEXT_MESSAGE]->(next)
                RETURN count(*) AS linked
            }
            WITH messages, batchIds
            MATCH (conv:Conversation {id: messages[0].conversation_id})
            MATCH (first:Message {id: messages[0].id})
            OPTIONAL MATCH (conv)-[:HAS_MESSAGE]->(prev:Message)
            WHERE NOT prev.id IN batchIds
            WITH messages, first, prev
            ORDER BY prev.timestamp DESC
            WITH messages, first, head(collect(prev)) AS prev
            FOREACH (_ IN CASE WHEN prev IS NULL THEN [] ELSE [1] END |
                MERGE (prev)-[:NEXT_MESSAGE]->(first)
            )
            WITH messages
            UNWIND messages AS msg
            WITH DISTINCT msg.id AS id
            MATCH (m:Message {id: id})
            RETURN m
            ORDER BY m.timestamp";

    /// <summary>Create NEXT_MESSAGE link between two specific messages. MERGE (not CREATE) for the same
    /// idempotency guarantee as <see cref="LinkNextMessage"/>.</summary>
    public const string CreateNextMessageLink =
        "MATCH (prev:Message {id: $prevId}), (next:Message {id: $nextId}) MERGE (prev)-[:NEXT_MESSAGE]->(next)";

    /// <summary>Connect first batch message to the existing last message in the conversation. MERGE (not
    /// CREATE) for the same idempotency guarantee as <see cref="LinkNextMessage"/>.</summary>
    public const string LinkBatchToExisting = @"
                    MATCH (conv:Conversation {id: $conversationId})-[:HAS_MESSAGE]->(prev:Message)
                    WHERE NOT prev.id IN $batchIds
                    WITH prev ORDER BY prev.timestamp DESC LIMIT 1
                    MATCH (first:Message {id: $firstId})
                    MERGE (prev)-[:NEXT_MESSAGE]->(first)";

    /// <summary>Re-read all created messages by ids ordered by timestamp.</summary>
    public const string GetByIds =
        "MATCH (m:Message) WHERE m.id IN $ids RETURN m ORDER BY m.timestamp";

    // ── GetByIdAsync ───────────────────────────────────────────────────

    /// <summary>Get a single message by id.</summary>
    public const string GetById = "MATCH (m:Message {id: $id}) RETURN m";

    // ── GetByConversationAsync ─────────────────────────────────────────

    /// <summary>Get all messages in a conversation ordered by timestamp.</summary>
    public const string GetByConversation = @"
            MATCH (c:Conversation {id: $conversationId})-[:HAS_MESSAGE]->(m:Message)
            RETURN m
            ORDER BY m.timestamp";

    // ── GetRecentBySessionAsync ────────────────────────────────────────

    /// <summary>Get recent messages for a session, ordered by timestamp descending.</summary>
    public const string GetRecentBySession = @"
            MATCH (m:Message {session_id: $sessionId})
            RETURN m
            ORDER BY m.timestamp DESC
            LIMIT $limit";

    // ── GetAllBySessionAsync ───────────────────────────────────────────

    /// <summary>Get ALL messages for a session in chronological (oldest-first) order, with no cap.
    /// Used by retroactive whole-session extraction, which must see every message — unlike the recall
    /// path (<see cref="GetRecentBySession"/>), which is intentionally capped and newest-first.</summary>
    public const string GetAllBySession = @"
            MATCH (m:Message {session_id: $sessionId})
            RETURN m
            ORDER BY m.timestamp";

    // ── SearchByVectorAsync ────────────────────────────────────────────

    /// <summary>
    /// Builds a vector similarity search query for messages with optional session and metadata filters.
    /// Session-scoped search uses the indexed Conversation session id, traverses HAS_MESSAGE, and
    /// calculates exact cosine inside that session; unscoped search uses the global vector index.
    /// </summary>
    /// <param name="hasSessionFilter">When true, scopes traversal through <c>Conversation.session_id</c>.</param>
    /// <param name="metadataFilterFragment">
    /// Optional pre-formatted AND condition lines from <see cref="MetadataFilterBuilder.Build"/>.
    /// </param>
    /// <param name="topK">Number of candidates to retrieve from the unscoped vector index.</param>
    public static string SearchByVector(bool hasSessionFilter, string? metadataFilterFragment = null, int topK = 10)
    {
        if (hasSessionFilter)
        {
            return $$"""
                MATCH (:Conversation {session_id: $sessionId})-[:HAS_MESSAGE]->(node:Message)
                WHERE node.embedding IS NOT NULL
                {{metadataFilterFragment}}
                WITH node, vector.similarity.cosine(node.embedding, $embedding) AS score
                WHERE score >= $minScore
                RETURN node, score
                ORDER BY score DESC
                LIMIT $limit
                """;
        }

        return new CypherBuilder()
            .WithVectorSearch("message_embedding_idx", "$embedding", "node", topK)
            .Where("score >= $minScore")
            .AndRawFragment(metadataFilterFragment)
            .Return("node, score")
            .OrderBy("score DESC")
            .Limit("$limit", when: !string.IsNullOrWhiteSpace(metadataFilterFragment))
            .Build();
    }

    // ── DeleteBySessionAsync ───────────────────────────────────────────

    /// <summary>Delete all messages for a given session.</summary>
    public const string DeleteBySession =
        "MATCH (m:Message {session_id: $sessionId}) DETACH DELETE m";

    // ── DeleteAsync ────────────────────────────────────────────────────

    /// <summary>Delete a message and all its relationships (DETACH DELETE).</summary>
    public const string DeleteCascade = @"
            MATCH (m:Message {id: $id})
            DETACH DELETE m
            RETURN true AS deleted";

    /// <summary>Delete a message node only (preserve relationships).</summary>
    public const string DeleteSimple = @"
            MATCH (m:Message {id: $id})
            DELETE m
            RETURN true AS deleted";
}
