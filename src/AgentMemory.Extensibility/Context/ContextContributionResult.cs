using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>Exactly one section or an explicit omission; a refusal is not a searched-and-empty result.</summary>
[Experimental("AMEXT001")]
public sealed record ContextContributionResult
{
    /// <summary>Produced section, present only when no omission is reported.</summary>
    public ContextSection? Section { get; init; }
    /// <summary>Why no section was returned; required when Section is absent.</summary>
    public ContextOmissionReason? OmissionReason { get; init; }
    /// <summary>Optional diagnostic detail for an omission; never rendered as memory content.</summary>
    public string? Detail { get; init; }
}
