using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Decides, memory by memory, which memories a recall found are put in front of the assistant: the judge at the fan-in.
/// </summary>
/// <remarks>
/// <para>
/// Every memory type searches; the gate is shown every memory found, with the turn, the conversation and the date, and
/// returns for each the probability that the reply needs it. A caller keeps what reaches its threshold. Measured on
/// labelled turns and two unseen sets, a judge at this point served 96% of the turns any memory type could serve, against
/// 68–77% for a similarity floor, with less than half the clutter; deciding which types to search before the search
/// served less than searching them all.
/// </para>
/// <para>
/// A gate calls a model and can fail or be slow. It must not hide that: throw, or let the caller's timeout cancel it, and
/// the caller falls back to the recall it would have made without a gate. It must never return a partial decision as if
/// it were whole.
/// </para>
/// </remarks>
[Experimental("AMGATE001")]
public interface IMemoryGate
{
    /// <summary>The probability, for every candidate, that the reply to the turn needs it.</summary>
    Task<MemoryGateDecision> DecideAsync(MemoryGateRequest request, CancellationToken cancellationToken = default);
}

/// <summary>One memory a recall found, as the gate is shown it.</summary>
/// <param name="Key">Unique within the request; the decision answers by it.</param>
/// <param name="MemoryType">
/// The kind of memory it is: <c>semantic</c> (facts), <c>entity-graph</c> (people, places and how they connect),
/// <c>preference</c>, <c>episodic</c> (what was said), <c>procedural</c> and <c>reasoning</c> (how tasks went),
/// <c>prospective</c> (due or expiring).
/// </param>
/// <param name="Text">The memory as the gate reads it, with its dates and what it is.</param>
[Experimental("AMGATE001")]
public sealed record MemoryGateCandidate(string Key, string MemoryType, string Text);

/// <summary>An earlier turn of the conversation the gate is shown.</summary>
[Experimental("AMGATE001")]
public sealed record MemoryGateTurn(string Role, string Text);

/// <summary>What the gate decides about.</summary>
/// <param name="Turn">The message the assistant is about to reply to.</param>
/// <param name="Conversation">The turns before it, oldest first.</param>
/// <param name="Now">The moment of the turn, for dates.</param>
/// <param name="Candidates">Every memory the recall found.</param>
[Experimental("AMGATE001")]
public sealed record MemoryGateRequest(
    string Turn, IReadOnlyList<MemoryGateTurn> Conversation, DateTimeOffset Now, IReadOnlyList<MemoryGateCandidate> Candidates);

/// <summary>The gate's answer.</summary>
/// <param name="Probabilities">P(the reply needs it), by candidate key; every candidate has one.</param>
/// <param name="AnsweredBy">Which judges answered (for diagnostics): one name, or several joined.</param>
[Experimental("AMGATE001")]
public sealed record MemoryGateDecision(IReadOnlyDictionary<string, double> Probabilities, string AnsweredBy);
