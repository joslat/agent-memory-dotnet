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
