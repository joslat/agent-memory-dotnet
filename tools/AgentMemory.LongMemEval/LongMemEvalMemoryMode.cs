namespace AgentMemory.LongMemEval;

/// <summary>Explicit AgentMemory operating mode used by the LongMemEval adapter.</summary>
public enum LongMemEvalMemoryMode
{
    /// <summary>Persist and semantically recall raw messages only.</summary>
    Raw,

    /// <summary>Extract structured graph memory and exclude raw messages from answer recall.</summary>
    Structured,

    /// <summary>Recall both raw messages and extracted structured graph memory.</summary>
    Hybrid
}

internal static class LongMemEvalMemoryModeExtensions
{
    public static string Fingerprint(this LongMemEvalMemoryMode mode) => mode switch
    {
        LongMemEvalMemoryMode.Raw => "raw-message-vector-control",
        LongMemEvalMemoryMode.Structured => "structured-graph",
        LongMemEvalMemoryMode.Hybrid => "hybrid-message-and-graph",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    public static bool UsesExtraction(this LongMemEvalMemoryMode mode) =>
        mode is LongMemEvalMemoryMode.Structured or LongMemEvalMemoryMode.Hybrid;

    public static bool UsesRawRecall(this LongMemEvalMemoryMode mode) =>
        mode is LongMemEvalMemoryMode.Raw or LongMemEvalMemoryMode.Hybrid;
}
