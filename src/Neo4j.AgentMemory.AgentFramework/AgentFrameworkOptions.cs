namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// Top-level options for the Agent Framework memory adapter.
/// </summary>
public sealed class AgentFrameworkOptions
{
    public ContextFormatOptions ContextFormat { get; set; } = new();
    public bool AutoExtractOnPersist { get; set; } = true;
    public bool PersistReasoningTraces { get; set; } = false;
    public string DefaultSessionIdHeader { get; set; } = "X-Session-Id";
    public string DefaultConversationIdHeader { get; set; } = "X-Conversation-Id";
}
