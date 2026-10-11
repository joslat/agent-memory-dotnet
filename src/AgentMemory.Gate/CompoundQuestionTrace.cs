using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Gate;

/// <summary>Content-free diagnostics for optional compound judging; absent when the feature is off.</summary>
[Experimental("AMGATE001")]
public sealed record CompoundQuestionTrace
{
    /// <summary>The context metadata key holding this trace.</summary>
    public const string MetadataKey = "gate.compound";

    /// <summary>split, partial, baseline, or not-needed.</summary>
    public required string Outcome { get; init; }

    /// <summary>A bounded reason code for a skipped or incomplete attempt; never provider or question text.</summary>
    public string? Reason { get; init; }

    /// <summary>Number of validated focuses proposed, at most three.</summary>
    public required int PlannedParts { get; init; }

    /// <summary>Number of complete, valid part decisions incorporated.</summary>
    public required int CompletedParts { get; init; }

    /// <summary>Additional gate invocations, including failures; not a count of physical provider requests.</summary>
    public required int AdditionalGateDecisions { get; init; }

    /// <summary>Time spent on optional planning and part judging, excluding baseline judging.</summary>
    public required long ElapsedMilliseconds { get; init; }
}
