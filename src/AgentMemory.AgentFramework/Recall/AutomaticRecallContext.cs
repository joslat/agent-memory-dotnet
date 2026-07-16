using Microsoft.Extensions.AI;

namespace AgentMemory.AgentFramework.Recall;

/// <summary>
/// Input to <see cref="IAutomaticRecallPolicy.DecideAsync"/>: everything about the current turn a policy
/// needs in order to decide whether/what to recall, before <c>Neo4jMemoryContextProvider</c> builds the
/// <c>RecallRequest</c> (#88).
/// </summary>
public sealed record AutomaticRecallContext
{
    /// <summary>
    /// This turn's user messages (already filtered to <c>ChatRole.User</c> with non-blank text) -- the
    /// same set <c>Neo4jMemoryContextProvider</c> joins into the recall query text.
    /// </summary>
    public required IReadOnlyList<ChatMessage> Messages { get; init; }

    /// <summary>The session this turn belongs to.</summary>
    public required string SessionId { get; init; }

    /// <summary>The conversation this turn belongs to.</summary>
    public required string ConversationId { get; init; }

    /// <summary>
    /// The authenticated owner for this turn, when R1 multi-user isolation is in use. Null for a
    /// shared/global (unowned) turn.
    /// </summary>
    public string? UserId { get; init; }
}
