using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Neo4j.AgentMemory.McpServer.Prompts;

/// <summary>
/// MCP prompt that initializes a memory-aware conversation session.
/// </summary>
[McpServerPromptType]
public sealed class MemoryConversationPrompt
{
    /// <summary>
    /// Returns system instructions for starting a memory-aware conversation.
    /// Loads context from memory and guides the agent on how to use memory tools.
    /// Use at the start of any conversation that should leverage stored memories.
    /// </summary>
    [McpServerPrompt(Name = "memory-conversation"), Description("Initialize a memory-aware conversation. Loads context from memory and instructs the agent on how to use memory tools throughout the conversation.")]
    public static IEnumerable<ChatMessage> MemoryConversation(
        [Description("Optional session identifier to scope memory retrieval")] string sessionId = "")
    {
        var sessionHint = string.IsNullOrEmpty(sessionId) ? "" : $" for session '{sessionId}'";
        var sessionArg = string.IsNullOrEmpty(sessionId) ? "" : $" (sessionId='{sessionId}')";

        yield return new ChatMessage(ChatRole.User,
            $"""
            Start a memory-aware conversation{sessionHint}.

            ## Available Memory Tools
            - **memory_get_context** — Retrieve assembled memory context (recent messages, entities, preferences, facts)
            - **memory_search** — Search memory for relevant context matching a query
            - **memory_store_message** — Persist a conversation message to short-term memory
            - **memory_add_entity** — Store a new entity (person, organization, location, event, object)
            - **memory_add_preference** — Record a user preference (communication style, tooling, format, etc.)
            - **memory_add_fact** — Store a factual statement as a subject–predicate–object triple

            ## Initialization Steps
            1. Call memory_get_context to load relevant memories{sessionArg}
            2. Review the loaded context for:
               - Previous conversation topics and decisions
               - Known user preferences
               - Relevant entities and relationships
            3. Greet the user, referencing relevant context if available

            ## During the Conversation
            - Call memory_store_message for important user messages
            - Call memory_add_preference when the user expresses a preference
            - Call memory_add_entity when new people, places, or organizations are mentioned
            - Call memory_search if the user asks about past interactions

            ## Best Practices
            - Always include sessionId='{(string.IsNullOrEmpty(sessionId) ? "default" : sessionId)}' on every tool call to scope memory correctly
            - Use confidence 0.9 for explicitly stated preferences; 0.75 for inferred ones
            - Store messages progressively — do not batch at the end of a conversation
            """);
    }
}
