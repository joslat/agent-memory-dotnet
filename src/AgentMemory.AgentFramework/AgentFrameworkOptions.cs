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
    /// Drops recalled chat history the host is already sending in the live thread.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider sees the full live thread and, until now, discarded it — so recall's
    /// <c>RecentMessages</c> re-sent turns the model was already being given, and the host paid for
    /// both copies.
    /// </para>
    /// <para>
    /// Filtered <b>before</b> <c>MaxChatHistoryMessages</c> applies, so this is a quality change as
    /// much as a cost one: the same budget then carries that many genuinely new messages instead of
    /// duplicates of the current turn.
    /// </para>
    /// <para>
    /// Matching is on <b>content only</b>, never role. <c>RecalledMessageRoleGate</c> rewrites a
    /// recalled message's role — privileged down to user below the trust threshold — while leaving its
    /// content identical, so a role-keyed comparison would miss every match on exactly the hosts that
    /// raised <c>MinimumTrustForSystemRole</c>.
    /// </para>
    /// <para>
    /// On by default: sending the model two copies of the same turn has no upside, and the comparison
    /// is a hash set over the thread the provider already holds.
    /// </para>
    /// </remarks>
    public bool DeduplicateRecalledHistory { get; set; } = true;

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

    /// <summary>
    /// When <see langword="true"/>, <c>Neo4jMemoryContextProvider</c> surfaces the six standard memory
    /// tools (<c>Tools.MemoryToolFactory.CreateAIFunctions()</c>) via <c>AIContext.Tools</c> on every
    /// invocation, so <c>AIContextProviders = [memoryProvider]</c> alone is enough to give the agent
    /// LLM-callable memory tools -- no separate <c>ChatOptions.Tools = [.. memoryTools]</c> wiring needed.
    /// </summary>
    /// <remarks>
    /// Defaults to <see langword="false"/>. <c>AddAgentMemoryFramework</c> registers
    /// <c>Tools.MemoryToolFactory</c> unconditionally, and the tools it creates include write-capable
    /// ones (<c>remember_fact</c>, <c>remember_preference</c>) -- so this must stay opt-in. Enabling it
    /// only because the factory exists in DI would silently hand every context-provider-wired agent new
    /// write tools on a package upgrade.
    /// </remarks>
    public bool ExposeMemoryToolsFromContextProvider { get; set; } = false;

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

    /// <summary>
    /// Injects a "what changed since we last spoke" block when a session resumes after a gap.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Off by default, and the off state is byte-identical</b> — no extra query, no extra message,
    /// nothing added to the prompt. A host opts in; an upgrade never does it for them.
    /// </para>
    /// <para>
    /// The delta <b>complements</b> recall on a resume turn, it does not replace it: the current
    /// question still needs relevance-ranked context.
    /// </para>
    /// </remarks>
    public bool InjectDeltaOnSessionResume { get; set; }

    /// <summary>
    /// The <c>StateBag</c> key holding the delta checkpoint (an ISO-8601 instant).
    /// </summary>
    /// <remarks>
    /// The checkpoint is a caller-held token, not a stored node — it rides the session's own
    /// serialize/restore seam, exactly as the identity keys do, so no schema pays for it.
    /// </remarks>
    public string DefaultDeltaCheckpointKey { get; set; } = "memory_delta_checkpoint";

    /// <summary>
    /// How stale the checkpoint must be before a turn counts as a <i>resume</i>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// There is no session lifecycle in this system — a session is a string — so "resume" cannot be
    /// detected from a close event that does not exist. An age threshold is deterministic, needs no
    /// state beyond the token, and is wrong only in the benign direction: a long pause inside one
    /// sitting yields a small, accurate delta rather than a wrong one.
    /// </para>
    /// </remarks>
    public TimeSpan MinimumDeltaGap { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Per-bucket cap on delta items. Exceeding it is reported <i>in the rendered block</i>, so
    /// truncation is visible rather than silently mistaken for "that was everything".
    /// </summary>
    public int MaxDeltaItemsPerSection { get; set; } = 20;
}
