namespace Neo4j.AgentMemory.AgentFramework;

/// <summary>
/// Top-level options for the Agent Framework memory adapter.
/// </summary>
public sealed class AgentFrameworkOptions
{
    /// <summary>
    /// Formatting options that control which memory categories are injected and how the context
    /// block is shaped. Defaults map these settings into <see cref="ContextFormatOptions"/>.
    /// </summary>
    public ContextFormatOptions ContextFormat { get; set; } = new();

    /// <summary>
    /// When <see langword="true"/>, the memory service runs extraction (entity/fact/preference) 
    /// automatically each time a message is persisted. Set to <see langword="false"/> to extract 
    /// on a background schedule instead.
    /// </summary>
    public bool AutoExtractOnPersist { get; set; } = true;

    /// <summary>
    /// When <see langword="true"/>, reasoning traces produced by <see cref="AgentTraceRecorder"/> 
    /// are persisted to the Neo4j graph. Disabled by default to reduce write overhead.
    /// </summary>
    public bool PersistReasoningTraces { get; set; } = false;

    // Breaking change (P2-2): renamed from DefaultSessionIdHeader/DefaultConversationIdHeader.
    // These are StateBag keys, not HTTP headers. Defaults updated to idiomatic StateBag key names.

    /// <summary>
    /// The key used to look up the session identifier in the MAF <c>StateBag</c>.
    /// Defaults to <c>"session_id"</c> — the idiomatic StateBag key name.
    /// </summary>
    public string DefaultSessionIdKey { get; set; } = "session_id";

    /// <summary>
    /// The key used to look up the conversation identifier in the MAF <c>StateBag</c>.
    /// Defaults to <c>"conversation_id"</c> — the idiomatic StateBag key name.
    /// </summary>
    public string DefaultConversationIdKey { get; set; } = "conversation_id";
}
