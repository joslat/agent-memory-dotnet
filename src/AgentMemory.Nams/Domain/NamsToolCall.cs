using System.Text.Json.Serialization;

namespace AgentMemory.Nams.Domain;

/// <summary>
/// A recorded tool call -- covers both <c>POST /v1/reasoning/tool-calls</c>'s create-response (which only
/// echoes <see cref="Id"/>/<see cref="StepId"/>/<see cref="ToolName"/>/<see cref="Status"/>) and the trace
/// endpoint's shape (which includes <see cref="Input"/>/<see cref="Output"/>/<see cref="DurationMs"/>/
/// <see cref="CreatedAt"/>), confirmed live (Phase 10e/10h). <see cref="Input"/>/<see cref="Output"/> are
/// JSON-encoded strings on the wire, not nested objects -- NAMS stores them as scalar string properties.
/// </summary>
internal sealed record NamsToolCall(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("stepId")] string? StepId,
    [property: JsonPropertyName("toolName")] string ToolName,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("input")] string? Input,
    [property: JsonPropertyName("output")] string? Output,
    [property: JsonPropertyName("durationMs")] int? DurationMs,
    [property: JsonPropertyName("createdAt")] string? CreatedAt);
