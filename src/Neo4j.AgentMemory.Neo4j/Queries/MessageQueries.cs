namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Message operations.
/// Each constant corresponds to exactly one repository method in
/// <see cref="Neo4j.AgentMemory.Neo4j.Repositories.Neo4jMessageRepository"/>.
/// </summary>
public static class MessageQueries
{
    // ── AddAsync ───────────────────────────────────────────────────────

    /// <summary>Create a message and link it to its conversation via HAS_MESSAGE.</summary>
    public const string Add = @"
            MATCH (conv:Conversation {id: $conversationId})
            CREATE (m:Message {
                id:              $id,
                conversation_id: $conversationId,
                session_id:      $sessionId,
                role:            $role,
                content:         $content,
                timestamp:       datetime($timestamp),
                tool_call_ids:   $toolCallIds,
                metadata:        $metadata
            })
            CREATE (conv)-[:HAS_MESSAGE]->(m)
            RETURN m";

    /// <summary>Link the first message in a conversation via FIRST_MESSAGE.</summary>
    public const string CreateFirstMessageLink = @"
                MATCH (conv:Conversation {id: $conversationId}), (m:Message {id: $id})
                WHERE NOT EXISTS { MATCH (conv)-[:FIRST_MESSAGE]->() }
                MERGE (conv)-[:FIRST_MESSAGE]->(m)";

    /// <summary>Establish NEXT_MESSAGE linked-list pointer from previous last message.</summary>
    public const string LinkNextMessage = @"
            MATCH (conv:Conversation {id: $conversationId})-[:HAS_MESSAGE]->(prev:Message)
            WHERE prev.id <> $id
            WITH prev ORDER BY prev.timestamp DESC LIMIT 1
            MATCH (m:Message {id: $id})
            CREATE (prev)-[:NEXT_MESSAGE]->(m)";

    // ── AddBatchAsync ──────────────────────────────────────────────────

    /// <summary>Batch create messages and link to conversations via UNWIND.</summary>
    public const string AddBatch = @"
            UNWIND $messages AS msg
            MATCH (conv:Conversation {id: msg.conversation_id})
            CREATE (m:Message {
                id:              msg.id,
                conversation_id: msg.conversation_id,
                session_id:      msg.session_id,
                role:            msg.role,
                content:         msg.content,
                timestamp:       datetime(msg.timestamp),
                tool_call_ids:   msg.tool_call_ids,
                metadata:        msg.metadata
            })
            CREATE (conv)-[:HAS_MESSAGE]->(m)
            RETURN m";

    /// <summary>Create NEXT_MESSAGE link between two specific messages.</summary>
    public const string CreateNextMessageLink =
        "MATCH (prev:Message {id: $prevId}), (next:Message {id: $nextId}) CREATE (prev)-[:NEXT_MESSAGE]->(next)";

    /// <summary>Connect first batch message to the existing last message in the conversation.</summary>
    public const string LinkBatchToExisting = @"
                    MATCH (conv:Conversation {id: $conversationId})-[:HAS_MESSAGE]->(prev:Message)
                    WHERE NOT prev.id IN $batchIds
                    WITH prev ORDER BY prev.timestamp DESC LIMIT 1
                    MATCH (first:Message {id: $firstId})
                    CREATE (prev)-[:NEXT_MESSAGE]->(first)";

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

    // ── SearchByVectorAsync ────────────────────────────────────────────

    /// <summary>
    /// Builds a vector similarity search query for messages with optional session and metadata filters.
    /// </summary>
    public static string SearchByVector(string sessionFilter, string filterClause) => $@"
            CALL db.index.vector.queryNodes('message_embedding_idx', $limit, $embedding)
            YIELD node, score
            WHERE score >= $minScore {sessionFilter}
            {filterClause}
            RETURN node, score
            ORDER BY score DESC";

    // ── DeleteBySessionAsync ───────────────────────────────────────────

    /// <summary>Delete all messages for a given session.</summary>
    public const string DeleteBySession =
        "MATCH (m:Message {session_id: $sessionId}) DETACH DELETE m";
}
