using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Neo4j.AgentMemory.McpServer.Prompts;

/// <summary>
/// MCP prompt that guides the agent through reviewing and auditing stored memories.
/// </summary>
[McpServerPromptType]
public sealed class MemoryReviewPrompt
{
    /// <summary>
    /// Returns instructions for reviewing all stored knowledge and flagging contradictions.
    /// Summarizes entities, preferences, facts, and recent conversations, then identifies
    /// potential contradictions or outdated information.
    /// </summary>
    [McpServerPrompt(Name = "memory-review"), Description("Review all stored knowledge and flag contradictions. Summarizes entities, preferences, facts, and recent conversations, then identifies potential contradictions or outdated information.")]
    public static IEnumerable<ChatMessage> MemoryReview()
    {
        yield return new ChatMessage(ChatRole.User,
            """
            Review everything stored in memory and provide a comprehensive summary.

            ## Available Memory Tools
            - **memory_search** — Search memory with a query and optional memory_types filter
            - **memory_get_context** — Get a full assembled context snapshot
            - **memory_list_sessions** — List all conversation sessions
            - **memory_get_entity** — Get full details of a specific entity by ID

            ## Review Steps
            1. Call memory_search with a broad query (e.g., "all") to retrieve entities
            2. Call memory_search with memory_types=['preferences'] to retrieve preferences
            3. Call memory_search with memory_types=['facts'] to retrieve stored facts
            4. Call memory_list_sessions to review conversation history
            5. For the most relevant entities, call memory_get_entity for full relationship details
            6. Compile a summary organized by:
               - **Known entities** — people, organizations, locations (with relationship graph)
               - **Stored preferences** — grouped by category (communication, format, tools, etc.)
               - **Key facts** — subject–predicate–object triples with confidence scores
               - **Recent conversation topics** — themes and decisions from recent sessions

            ## Quality Checks
            6. Flag any potential contradictions (e.g., conflicting facts about the same entity)
            7. Identify outdated preferences that may no longer apply
            8. Suggest preferences or facts that should be updated or removed
            9. Note any entities that appear in conversations but have not been formally stored

            ## Best Practices
            - Be thorough — a memory review is a maintenance operation, not a quick summary
            - Confidence scores below 0.6 should be highlighted for verification
            - Cross-reference entity mentions across multiple sessions to detect inconsistencies
            """);
    }
}
