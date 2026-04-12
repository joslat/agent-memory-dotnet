namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// Controls how memory context is formatted into chat messages.
/// </summary>
public sealed class ContextFormatOptions
{
    public bool IncludeEntities { get; set; } = true;
    public bool IncludeFacts { get; set; } = true;
    public bool IncludePreferences { get; set; } = true;
    public bool IncludeReasoningTraces { get; set; } = false;
    public string ContextPrefix { get; set; } = "The following context from memory is relevant to this conversation:";
    public int MaxContextMessages { get; set; } = 10;
}
