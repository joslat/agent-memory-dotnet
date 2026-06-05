namespace AgentMemory.AgentFramework;

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

    /// <summary>
    /// The key used to look up the user/owner identifier in the MAF <c>StateBag</c> (R1, multi-user
    /// isolation). Defaults to <c>"user_id"</c>. When present, it scopes recall to that owner's
    /// memories (plus shared/global) and stamps it as the owner on newly extracted knowledge. Absent
    /// ⇒ shared/global behavior, unchanged from before.
    /// </summary>
    public string DefaultUserIdKey { get; set; } = "user_id";

    /// <summary>
    /// The key used to look up the application / memory-store identifier in the MAF <c>StateBag</c>
    /// (R1b, store isolation). Defaults to <c>"application_id"</c>. When present and a writable
    /// <see cref="AgentMemory.Abstractions.Services.IMemoryStoreContext"/> is registered, it routes
    /// the store for the scope. Absent ⇒ the default store.
    /// </summary>
    public string DefaultApplicationIdKey { get; set; } = "application_id";
}
