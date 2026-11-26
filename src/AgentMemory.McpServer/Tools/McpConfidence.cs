namespace AgentMemory.McpServer.Tools;

/// <summary>
/// RVW-REST-HOSTS (the review's hosts-6): the add tools stored whatever confidence the caller sent. A model told to (or
/// injected into) sending confidence = 1000000 put its fact first in every ranking ordered by confidence, and decay never
/// brought it under the threshold. The host's own default is validated to 0..1; a per-call value now is too.
/// </summary>
internal static class McpConfidence
{
    /// <summary>The error result for a per-call confidence outside 0..1 (or not a number); null when it is fine or absent.</summary>
    public static string? Refusal(double? confidence) =>
        confidence is { } value && !(value is >= 0 and <= 1)
            ? ToolJsonContext.Serialize(new { error = $"confidence must be between 0 and 1; got {value}." })
            : null;
}
