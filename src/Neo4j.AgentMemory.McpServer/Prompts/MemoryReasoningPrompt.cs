using System.ComponentModel;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Server;

namespace Neo4j.AgentMemory.McpServer.Prompts;

/// <summary>
/// MCP prompt that guides the agent through structured reasoning with trace recording.
/// </summary>
[McpServerPromptType]
public sealed class MemoryReasoningPrompt
{
    /// <summary>
    /// Returns instructions for recording a structured reasoning trace for a complex task.
    /// Useful for debugging and learning from successful problem-solving approaches.
    /// </summary>
    [McpServerPrompt(Name = "memory-reasoning"), Description("Record a structured reasoning trace for a complex task. Guides the agent through step-by-step reasoning with trace recording so problem-solving approaches can be learned and replayed.")]
    public static IEnumerable<ChatMessage> MemoryReasoning(
        [Description("The task or problem to reason through and record")] string task)
    {
        yield return new ChatMessage(ChatRole.User,
            $"""
            Solve this task and record your reasoning: {task}

            ## Available Reasoning Tools
            - **memory_start_trace** — Begin a new reasoning trace for a task
            - **memory_record_step** — Record a single reasoning step (thought, action, observation)
            - **memory_complete_trace** — Finalize the trace with an outcome and success flag
            - **memory_search** — Search previous traces for similar problems and solutions

            ## Reasoning Steps
            1. Call memory_start_trace with the task description
            2. Before acting, search for similar past traces with memory_search
            3. For each significant reasoning step:
               a. Think about what to do next (thought)
               b. Decide on an action or tool call (action)
               c. Execute the action and observe the result (observation)
               d. Call memory_record_step with thought, action, observation
               e. If a tool was used, include tool_name, tool_args, and tool_result
            4. When the task is complete:
               - Call memory_complete_trace with the outcome summary
               - Set success=true if completed successfully, false otherwise
            5. Summarize the reasoning process and final outcome

            ## Best Practices
            - Record steps incrementally — do not wait until the end
            - Be precise in the thought field: state what you know and what you need to find out
            - Include tool arguments verbatim so traces can be replayed
            - Even failed attempts are valuable — record them with success=false and explain why
            """);
    }
}
