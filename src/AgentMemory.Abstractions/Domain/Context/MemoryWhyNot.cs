namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// G5 (PLAN 40.49): why one memory was or was not in a recall, and the gate that kept it out. Asked off the hot path,
/// for a person or a developer who expected a memory and did not see it.
/// </summary>
public sealed record MemoryWhyNot
{
    /// <summary>It was in the recall.</summary>
    public const string Recalled = "recalled";

    /// <summary>No memory has this id.</summary>
    public const string NotFound = "not-found";

    /// <summary>Another owner's memory: never recalled for this user.</summary>
    public const string Owner = "owner";

    /// <summary>The memory router did not read this kind for the question.</summary>
    public const string Router = "router";

    /// <summary>Closed by a change or a correction; <see cref="RelatedId"/> is what replaced it, when known.</summary>
    public const string Closed = "closed";

    /// <summary>Let go by decay.</summary>
    public const string Decayed = "decayed";

    /// <summary>Invalidated (forgotten or retracted).</summary>
    public const string Invalidated = "invalidated";

    /// <summary>Outside its valid time at the recall's instant, and the recall reads valid time.</summary>
    public const string Validity = "validity";

    /// <summary>Scored below the similarity floor; <see cref="Score"/> and <see cref="Floor"/> say by how much.</summary>
    public const string Similarity = "similarity";

    /// <summary>Above the floor, and outranked: the kind's cap was filled by memories that scored higher.</summary>
    public const string Rank = "rank";

    /// <summary>Above the floor, and cut to fit the context budget.</summary>
    public const string Budget = "budget";

    /// <summary>The memory asked about.</summary>
    public required string ItemId { get; init; }

    /// <summary>One of the gate tokens above (<see cref="Recalled"/> when nothing kept it out).</summary>
    public required string Gate { get; init; }

    /// <summary>The gate, in a sentence.</summary>
    public required string Detail { get; init; }

    /// <summary>The memory's similarity to the question, on the store's scale (0 to 1), when it was scored.</summary>
    public double? Score { get; init; }

    /// <summary>The recall's similarity floor, when the score matters.</summary>
    public double? Floor { get; init; }

    /// <summary>A memory the gate points to (the successor of a closed fact).</summary>
    public string? RelatedId { get; init; }
}
