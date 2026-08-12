namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Request to extract structured memory from messages.
/// </summary>
public sealed record ExtractionRequest
{
    /// <summary>
    /// Messages to extract from.
    /// </summary>
    public required IReadOnlyList<Message> Messages { get; init; }

    /// <summary>
    /// Earlier turns supplied only so <see cref="Messages"/> can be understood (E2).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing is extracted from these and nothing is attributed to them — they exist so
    /// "I moved there last year" can resolve the place named a few turns earlier. Empty by default,
    /// which is exactly the pre-E2 behaviour.
    /// </para>
    /// <para>
    /// <b>Kept separate from <see cref="Messages"/> rather than prepended to it</b>, because
    /// extracting from them would re-assert stored facts — and a re-assertion now earns confidence
    /// (S2) and increments the <c>mention_count</c> the salience reranker reads (R7). A fact would
    /// gain both every time it happened to sit inside a sliding window, so corroboration would
    /// quietly become recency.
    /// </para>
    /// </remarks>
    public IReadOnlyList<Message> ContextMessages { get; init; } = [];

    /// <summary>
    /// Session context for the extraction.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Optional user identifier.
    /// </summary>
    public string? UserId { get; init; }

    /// <summary>
    /// Types of memory to extract.
    /// </summary>
    public ExtractionTypes TypesToExtract { get; init; } = ExtractionTypes.All;

    /// <summary>
    /// Per-request override for the trust level stamped on everything persisted from this call (#92 Phase
    /// 3) -- e.g. a host importing a curated/verified document can pass
    /// <see cref="MemoryTrustLevel.ApplicationTrusted"/> or <see cref="MemoryTrustLevel.VerifiedExternal"/>
    /// for that one extraction. Null (the default) falls back to the configured
    /// <c>ExtractionOptions.DefaultTrustLevel</c>.
    /// </summary>
    public MemoryTrustLevel? TrustLevel { get; init; }

    /// <summary>
    /// Additional extraction options.
    /// </summary>
    public IReadOnlyDictionary<string, object> Options { get; init; } =
        new Dictionary<string, object>();
}
