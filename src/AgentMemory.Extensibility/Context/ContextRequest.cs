using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;

namespace AgentMemory.Extensibility.Context;

/// <summary>
/// What the host is asking for: who, in what session, for what purpose, within what budget.
/// </summary>
/// <remarks>
/// <para>
/// <b><see cref="Owner"/> enters the compiler as what the host CLAIMS, never what gets used.</b>
/// The compiler resolves it through the isolation policy before contributor code sees it, and
/// records that resolved result in <see cref="ContextSnapshot.Owner"/>.
/// </para>
/// <para>
/// <b>Every field here is read by something.</b> The design's larger request carries purpose, action
/// class, risk class, resource targets, required sections and hints; those arrive with the slices
/// that honour them. Shipping them now would put fields on a public, frozen-on-ship contract that no
/// code consumes — which is the defect this repository has found thirteen times in its own options,
/// and it would be worse here because a module author would reasonably set them and reasonably
/// expect something to happen.
/// </para>
/// </remarks>
[Experimental("AMEXT001")]
public sealed record ContextRequest
{
    /// <summary>The session whose context is being assembled.</summary>
    public required string SessionId { get; init; }

    /// <summary>The owner the host claims on input. Resolved through the isolation policy before use.</summary>
    public string? Owner { get; init; }

    /// <summary>The conversation, when the host distinguishes it from the session.</summary>
    public string? ConversationId { get; init; }

    /// <summary>The query driving retrieval, derived or explicit.</summary>
    public string? Query { get; init; }

    /// <summary>The recent turn(s), as the host supplies them.</summary>
    public IReadOnlyList<ChatMessage> RecentTurn { get; init; } = [];

    /// <summary>Valid-time pin: what was TRUE then. Null for a live read.</summary>
    public DateTimeOffset? AsOf { get; init; }

    /// <summary>Transaction-time pin: what was BELIEVED then. Null for a live read.</summary>
    public DateTimeOffset? SystemAsOf { get; init; }
}
