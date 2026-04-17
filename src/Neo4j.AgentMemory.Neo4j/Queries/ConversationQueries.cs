namespace Neo4j.AgentMemory.Neo4j.Queries;

/// <summary>
/// Centralized Cypher queries for Conversation operations.
/// </summary>
public static class ConversationQueries
{
    /// <summary>Upsert a Conversation node by id.</summary>
    public const string Upsert = @"
            MERGE (c:Conversation {id: $id})
            ON CREATE SET
                c.session_id  = $sessionId,
                c.user_id     = $userId,
                c.title       = $title,
                c.created_at  = datetime($createdAtUtc),
                c.updated_at  = datetime($updatedAtUtc),
                c.metadata    = $metadata
            ON MATCH SET
                c.session_id  = $sessionId,
                c.user_id     = $userId,
                c.title       = $title,
                c.updated_at  = datetime($updatedAtUtc),
                c.metadata    = $metadata
            RETURN c";

    /// <summary>Get a Conversation by id.</summary>
    public const string GetById = "MATCH (c:Conversation {id: $id}) RETURN c";

    /// <summary>Get all Conversations for a session, ordered by most recently updated.</summary>
    public const string GetBySession = @"
            MATCH (c:Conversation {session_id: $sessionId})
            RETURN c
            ORDER BY c.updated_at DESC";

    /// <summary>Delete a Conversation and all its relationships.</summary>
    public const string Delete = "MATCH (c:Conversation {id: $id}) DETACH DELETE c";

    // ── ListSessionsAsync ──────────────────────────────────────────────

    /// <summary>List sessions with conversation/message counts and last activity.</summary>
    public const string ListSessions = @"
            MATCH (c:Conversation)
            WITH c.session_id AS sessionId, collect(c) AS conversations
            OPTIONAL MATCH (c2:Conversation)-[:HAS_MESSAGE]->(m:Message)
            WHERE c2.session_id = sessionId
            WITH sessionId, SIZE(conversations) AS convCount, collect(m) AS messages
            RETURN sessionId,
                   convCount,
                   SIZE(messages) AS msgCount,
                   CASE WHEN SIZE(messages) > 0 THEN messages[-1].content ELSE null END AS lastPreview,
                   CASE WHEN SIZE(messages) > 0 THEN messages[-1].timestamp ELSE null END AS lastActivity
            ORDER BY lastActivity DESC
            LIMIT $limit";
}
