namespace Neo4j.AgentMemory.Abstractions.Domain;

/// <summary>
/// Result of context compression, containing tiered summaries and recent messages.
/// </summary>
public sealed class CompressedContext
{
    /// <summary>High-level reflections (tier 1 — most compressed).</summary>
    public IReadOnlyList<string> Reflections { get; init; } = [];

    /// <summary>Observation summaries of message groups (tier 2).</summary>
    public IReadOnlyList<string> Observations { get; init; } = [];

    /// <summary>Recent messages kept verbatim (tier 3 — most recent).</summary>
    public IReadOnlyList<Message> RecentMessages { get; init; } = [];

    /// <summary>Whether compression was applied.</summary>
    public bool WasCompressed { get; init; }

    /// <summary>Original token count before compression.</summary>
    public int OriginalTokenCount { get; init; }

    /// <summary>Estimated token count after compression.</summary>
    public int CompressedTokenCount { get; init; }
}
