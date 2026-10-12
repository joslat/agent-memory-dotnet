namespace AgentMemory.Abstractions.Domain;

/// <summary>How a request's targets should enter memory extraction.</summary>
public enum ExtractionInputMode
{
    /// <summary>Preserve the entry point's existing writer/extractor selection and configured options.</summary>
    Unspecified = 0,

    /// <summary>
    /// One target user message, with optional other-role targets and read-only context. Requires an
    /// enabled store-aware writer; its configured failure fallback still applies. Cannot be deferred
    /// with other turns or passed to multi-session extraction. Replay turns individually in order.
    /// </summary>
    ConversationTurn = 1,

    /// <summary>
    /// Document or retrospective input. Uses configured extractors, even for one user message, and
    /// never the store-aware writer. Token-bounded multi-session extraction remains a separate API.
    /// </summary>
    Import = 2,
}
