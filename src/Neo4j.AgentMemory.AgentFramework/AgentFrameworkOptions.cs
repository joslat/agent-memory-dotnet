namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// Top-level options for the Agent Framework memory adapter.
/// </summary>
public sealed class AgentFrameworkOptions
{
    public ContextFormatOptions ContextFormat { get; set; } = new();
    public bool AutoExtractOnPersist { get; set; } = true;
    public bool PersistReasoningTraces { get; set; } = false;
    // Breaking change (P2-2): renamed from DefaultSessionIdHeader/DefaultConversationIdHeader.
    // These are StateBag keys, not HTTP headers. Defaults updated to idiomatic StateBag key names.
    public string DefaultSessionIdKey { get; set; } = "session_id";
    public string DefaultConversationIdKey { get; set; } = "conversation_id";
}
