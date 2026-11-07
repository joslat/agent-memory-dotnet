namespace AgentMemory.Abstractions.Domain;

/// <summary>A period a question asked about, as recall resolved it (<c>RecallOptions.TimeWindows</c>).</summary>
public sealed record TimeWindow
{
    /// <summary>The first instant of the period, inclusive.</summary>
    public required DateTimeOffset StartUtc { get; init; }

    /// <summary>The last instant of the period, inclusive.</summary>
    public required DateTimeOffset EndUtc { get; init; }

    /// <summary>
    /// The event the question anchored the period on ("go-live" in "go-live week"), or null when the question named
    /// the period itself.
    /// </summary>
    public string? Anchor { get; init; }

    /// <summary>The dated fact that gave the anchored period its date, or null.</summary>
    public string? AnchorFactId { get; init; }
}
