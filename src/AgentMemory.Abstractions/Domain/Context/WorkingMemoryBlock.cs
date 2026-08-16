namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// The compiled, deterministic profile block for one owner.
/// </summary>
/// <remarks>
/// <para>
/// Everything else the system retrieves is probabilistic: query embedding → global vector top-K →
/// owner post-filter → similarity threshold. Owner starvation under that path is measured, not
/// theoretical — an owner's own facts inside the global top-60 averaged 7, minimum 1, and one real
/// question retrieved <b>zero</b> facts from a graph holding 504 of its own, all live, all above the
/// floor. This block is a point-read by owner and cannot be starved.
/// </para>
/// <para>
/// <see cref="ContentHash"/> exists to make rebuilds cheap and the text stable: a rebuild whose hash
/// matches the stored one writes nothing, so <see cref="BuiltAtUtc"/> moves only when the content
/// moves. Byte-stability also matters for prompt-prefix caching — a block that reshuffled itself on
/// every rebuild would defeat it.
/// </para>
/// </remarks>
public sealed record WorkingMemoryBlock
{
    /// <summary>The owner this block was compiled for.</summary>
    public required string OwnerId { get; init; }

    /// <summary>The rendered block, byte-stable between input changes.</summary>
    public required string Text { get; init; }

    /// <summary>When the content last changed — not when a rebuild last ran.</summary>
    public required DateTimeOffset BuiltAtUtc { get; init; }

    /// <summary>SHA-256 of <see cref="Text"/>, lowercase hex.</summary>
    public required string ContentHash { get; init; }
}
