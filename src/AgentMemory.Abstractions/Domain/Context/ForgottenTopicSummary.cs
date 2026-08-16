namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// A topic the system used to know something about and has since let go of.
/// </summary>
/// <remarks>
/// <para>
/// Forgetting works here and is <b>invisible</b>. Decay prunes, recall returns fewer facts, and the
/// agent answers as though it had never known — which is indistinguishable, to the person asking, from
/// never having been told. A memory system that cannot say "I used to know this" is one whose gaps all
/// look like the same gap.
/// </para>
/// <para>
/// <b>A summary, not the facts.</b> Rendering the forgotten content would undo the forgetting — the
/// decayed values would be back in the prompt, occupying budget, being answered from. What surfaces is
/// the <i>shape</i> of the absence: a topic, how much there was, and when it went. That is enough for
/// the agent to say "I no longer have details on X" and for the user to re-supply them, which is the
/// entire point.
/// </para>
/// </remarks>
public sealed record ForgottenTopicSummary
{
    /// <summary>The dominant subject among the decayed facts that matched.</summary>
    public required string Topic { get; init; }

    /// <summary>How many decayed facts shared that subject.</summary>
    public required int Count { get; init; }

    /// <summary>When the oldest of them was first learned.</summary>
    public DateTimeOffset? OldestUtc { get; init; }

    /// <summary>When the most recent of them aged out.</summary>
    public DateTimeOffset? AgedOutUtc { get; init; }
}
