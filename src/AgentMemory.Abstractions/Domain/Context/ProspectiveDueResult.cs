namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Facts that became due, and facts about to stop being true — selected by time, never by similarity.
/// </summary>
/// <remarks>
/// <para>
/// <b>The absence of a similarity score is the specification.</b> Every other retrieval channel is
/// reactive: it answers the question in front of it. A reminder is by definition off-topic — nobody
/// asks "is there anything I should know?" — so scoping firing by similarity to the current query
/// would reintroduce the exact failure it exists to fix.
/// </para>
/// <para>
/// Two lists rather than one, because they are different claims. A <see cref="Due"/> fact just became
/// true; an <see cref="Expiring"/> one is about to stop being true. Merging them would force a reader
/// to infer which from the dates, and a reminder that has to be decoded is a reminder that gets
/// ignored.
/// </para>
/// </remarks>
public sealed record ProspectiveDueResult
{
    /// <summary>Facts whose validity opened in the window.</summary>
    public IReadOnlyList<Fact> Due { get; init; } = Array.Empty<Fact>();

    /// <summary>Facts whose validity closes within the expiring window.</summary>
    public IReadOnlyList<Fact> Expiring { get; init; } = Array.Empty<Fact>();

    /// <summary>Nothing fired.</summary>
    public static ProspectiveDueResult Empty { get; } = new();

    /// <summary>True when neither list has anything in it.</summary>
    public bool IsEmpty => Due.Count == 0 && Expiring.Count == 0;
}
