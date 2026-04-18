namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// Controls how memory context is formatted into chat messages.
/// </summary>
public sealed class ContextFormatOptions
{
    /// <summary>
    /// When <see langword="true"/>, entity nodes retrieved from the memory graph are included in the
    /// context block injected before each agent turn.
    /// </summary>
    public bool IncludeEntities { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, factual statements stored in memory are included in the context block.
    /// </summary>
    public bool IncludeFacts { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, user preference records are included in the context block.
    /// </summary>
    public bool IncludePreferences { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, recent reasoning trace summaries are appended to the context block.
    /// Disabled by default to keep context concise.
    /// </summary>
    public bool IncludeReasoningTraces { get; set; } = false;

    /// <summary>
    /// System-message text prepended to the context block. Set to <see cref="string.Empty"/> to omit
    /// the prefix and use the full <see cref="MaxContextMessages"/> budget for memory items.
    /// </summary>
    public string ContextPrefix { get; set; } = "The following context from memory is relevant to this conversation:";

    /// <summary>
    /// Maximum number of chat messages to include in the context block (including the prefix system
    /// message). When <see cref="ContextPrefix"/> is non-empty, the effective limit for memory item
    /// messages is <c>MaxContextMessages - 1</c>.
    /// </summary>
    public int MaxContextMessages { get; set; } = 10;
}
