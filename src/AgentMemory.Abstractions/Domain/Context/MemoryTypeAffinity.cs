namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// Which memory type a recall sub-query is aimed at (Proposal M, 30.10).
/// </summary>
/// <remarks>
/// <para>
/// Deliberately <b>not</b> <c>[Flags]</c>. A sub-query targets exactly one memory type; that is the
/// whole point of fanning out rather than issuing one blended query, and a bitwise union would let a
/// caller express "semantic or temporal", which is the monolithic query this mechanism exists to
/// split.
/// </para>
/// <para>
/// Values start at 1 so that <c>default(MemoryTypeAffinity)</c> is not a silently-valid affinity — an
/// unset field is invalid rather than quietly Semantic.
/// </para>
/// </remarks>
public enum MemoryTypeAffinity
{
    /// <summary>Facts and entities reached by meaning.</summary>
    Semantic = 1,

    /// <summary>Questions anchored to a time or an interval.</summary>
    Temporal,

    /// <summary>Specific remembered episodes, sessions, or messages.</summary>
    Episodic,

    /// <summary>Stated likes, dislikes, and standing choices.</summary>
    Preference,

    /// <summary>How-to knowledge and previously-run procedures.</summary>
    Procedural,
}
