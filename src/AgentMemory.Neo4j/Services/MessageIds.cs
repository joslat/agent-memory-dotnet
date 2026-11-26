namespace AgentMemory.Neo4j.Services;

/// <summary>
/// RVW-REST-HOSTS (the review's hosts-3): the id a message is written under when another conversation already holds the
/// one it came with. The message write queries compute the same value in Cypher (<c>id + '@' + conversation</c>).
/// </summary>
internal static class MessageIds
{
    public static string ConversationQualified(string messageId, string conversationId) => $"{messageId}@{conversationId}";
}
