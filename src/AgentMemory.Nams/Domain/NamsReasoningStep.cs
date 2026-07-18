using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// A reasoning step -- covers both <c>POST /v1/reasoning/steps</c>'s create-response (which omits
/// <see cref="CreatedAt"/>) and the list/trace endpoints' shape (which includes it), confirmed live
/// (Phase 10e/10h). One shape covers both rather than two near-duplicate records, matching how
/// <see cref="NamsMessage"/> already covers multiple endpoints' near-identical shapes.
/// </summary>
internal sealed record NamsReasoningStep(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("conversationId")] string? ConversationId,
    [property: JsonPropertyName("reasoning")] string Reasoning,
    [property: JsonPropertyName("actionTaken")] string ActionTaken,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("createdAt")] string? CreatedAt);
