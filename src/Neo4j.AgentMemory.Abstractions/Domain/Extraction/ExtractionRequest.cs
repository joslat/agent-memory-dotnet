namespace Neo4j.AgentMemory.Abstractions.Domain;

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
    /// Additional extraction options.
    /// </summary>
    public IReadOnlyDictionary<string, object> Options { get; init; } =
        new Dictionary<string, object>();
}
