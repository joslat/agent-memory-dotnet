using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Context;

/// <summary>Limits for an explicitly composed module selection path. Existing rendering does not activate it.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleSelectionOptions
{
    /// <summary>Whether to invoke the explicitly supplied gate factory. Off never resolves or calls a gate.</summary>
    public bool UseJudge { get; init; }
    /// <summary>Minimum complete, finite judge score to retain an item; not a calibration claim.</summary>
    public double Threshold { get; init; } = 0.5;
    /// <summary>Total projected candidate allowance before deduplication.</summary>
    public int MaxCandidates { get; init; } = 128;
    /// <summary>Default projected item allowance for each contributor.</summary>
    public int MaxItemsPerContributor { get; init; } = 32;
    /// <summary>Explicit contributor-specific projected item allowances.</summary>
    public IReadOnlyDictionary<string, int> ContributorItemLimits { get; init; } = new Dictionary<string, int>().AsReadOnly();
    /// <summary>Maximum whole unique items in the selected output.</summary>
    public int MaxItems { get; init; } = 16;
    /// <summary>Maximum raw UTF-8 bytes of one candidate before host admission.</summary>
    public int MaxCandidateUtf8Bytes { get; init; } = 8192;
    /// <summary>Maximum total candidate-text UTF-8 bytes offered to the optional judge, excluding conversation.</summary>
    public int MaxJudgeUtf8Bytes { get; init; } = 32768;
    /// <summary>Exact module output UTF-8 allowance, including all delimiters/separators supplied by the renderer.</summary>
    public int MaxRenderedUtf8Bytes { get; init; } = 8192;
    /// <summary>Maximum preceding user/assistant messages copied into the module judge request.</summary>
    public int MaxConversationMessages { get; init; } = 6;
}

/// <summary>Host admission and exact module rendering, without taking a framework dependency.</summary>
[Experimental("AMEXT001")]
public interface IModuleCandidateRenderer
{
    /// <summary>Evaluates this untrusted item once; false forbids sending its text to a judge or model.</summary>
    bool Admit(ModuleContextCandidate candidate);
    /// <summary>Renders only previously admitted items. Must be deterministic and repeatable without reevaluating admission.</summary>
    /// <remarks>Include any separator appended to existing core instructions so byte accounting is exact.
    /// Never include the core block itself or truncate an item.</remarks>
    string Render(IReadOnlyList<ModuleContextCandidate> candidates);
}
