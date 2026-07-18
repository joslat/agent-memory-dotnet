using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>Result of <c>GET /v1/reasoning/trace/{conversationId}</c> -- a flat conversation-scoped bundle of
/// all recorded steps and tool calls (confirmed live, Phase 10e: flat, not steps-with-nested-toolCalls despite
/// the endpoint's own prose description implying nesting).</summary>
internal sealed record NamsReasoningTrace(
    [property: JsonPropertyName("conversationId")] string ConversationId,
    [property: JsonPropertyName("steps")] IReadOnlyList<NamsReasoningStep> Steps,
    [property: JsonPropertyName("toolCalls")] IReadOnlyList<NamsToolCall> ToolCalls);
