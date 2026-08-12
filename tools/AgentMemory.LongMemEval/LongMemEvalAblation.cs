namespace AgentMemory.LongMemEval;

/// <summary>A product capability that can be switched off to measure what it contributes.</summary>
/// <param name="Name">Stable identifier, recorded with the result.</param>
/// <param name="MemoryType">The memory type it is expected to serve.</param>
/// <param name="Option">The option a run sets to disable it, named so a report is reproducible.</param>
internal sealed record LongMemEvalCapability(string Name, string MemoryType, string Option)
{
    /// <summary>Assistant-turn capture — the episodic writer.</summary>
    internal static LongMemEvalCapability Episodic { get; } =
        new("episodic-capture", "episodic", "ExtractionOptions.AssistantContentMode");

    /// <summary>Valid-time gating on live recall plus the extractor that writes the bounds.</summary>
    internal static LongMemEvalCapability Prospective { get; } =
        new("valid-time", "temporal", "RecallOptions.ValidTime + LlmExtractionOptions.TemporalValidity");

    /// <summary>Reasoning-trace persistence and recall.</summary>
    internal static LongMemEvalCapability Traces { get; } =
        new("reasoning-traces", "procedural", "AgentFrameworkOptions.PersistReasoningTraces");

    internal static IReadOnlyList<LongMemEvalCapability> All { get; } =
        [Episodic, Prospective, Traces];
}

/// <summary>One question's behaviour across an ablation pair.</summary>
internal sealed record LongMemEvalQuestionFlip(
    string QuestionId,
    string MemoryType,
    bool CorrectWithCapability,
    bool CorrectWithout)
{
    /// <summary>The capability turned a wrong answer right — the case it is meant to produce.</summary>
    public bool Gained => CorrectWithCapability && !CorrectWithout;

    /// <summary>The capability turned a right answer wrong — the case nobody reports.</summary>
    public bool Lost => !CorrectWithCapability && CorrectWithout;
}

/// <summary>Result of ablating one capability.</summary>
internal sealed record LongMemEvalAblationResult(
    LongMemEvalCapability Capability,
    IReadOnlyList<LongMemEvalQuestionFlip> Flips,
    int QuestionsCompared)
{
    public IReadOnlyList<LongMemEvalQuestionFlip> Gains => Flips.Where(f => f.Gained).ToArray();
    public IReadOnlyList<LongMemEvalQuestionFlip> Losses => Flips.Where(f => f.Lost).ToArray();

    /// <summary>Net questions gained. Can be negative, and that must be reportable.</summary>
    public int Net => Gains.Count - Losses.Count;

    /// <summary>Net movement in accuracy points over the compared set.</summary>
    public double NetPoints => QuestionsCompared == 0 ? 0 : Net * 100.0 / QuestionsCompared;
}

/// <summary>
/// Compares a run with a capability on against the same run with it off.
/// </summary>
/// <remarks>
/// <para>
/// <b>Per-question, not aggregate, deliberately.</b> An aggregate hides the two things worth knowing:
/// <i>which</i> questions a capability rescued, and whether it broke any that previously worked. A
/// capability that gains three and loses three reports as neutral and is not — it is churn, and churn
/// is the signature of a change that is moving noise rather than adding signal.
/// </para>
/// <para>
/// <b>No vendor in the surveyed field publishes a per-memory-type ablation.</b> That is the whole
/// reason this exists: the differentiation available here is the measurement, not the tier.
/// </para>
/// <para>
/// <b>This does not decide anything on its own.</b> Its output must be read against the per-type noise
/// floor — a net movement smaller than what the same configuration already varied by is not a result.
/// </para>
/// </remarks>
internal static class LongMemEvalAblation
{
    /// <summary>
    /// Flips between the two arms, restricted to questions present and judged in BOTH.
    /// </summary>
    /// <remarks>
    /// A question judged in only one arm cannot be compared, and including it would let a run that
    /// simply answered fewer questions look like a capability effect.
    /// </remarks>
    internal static LongMemEvalAblationResult Compare(
        LongMemEvalCapability capability,
        IReadOnlyDictionary<string, bool> withCapability,
        IReadOnlyDictionary<string, bool> withoutCapability,
        IReadOnlyDictionary<string, string> memoryTypeByQuestionId)
    {
        ArgumentNullException.ThrowIfNull(capability);
        ArgumentNullException.ThrowIfNull(withCapability);
        ArgumentNullException.ThrowIfNull(withoutCapability);
        ArgumentNullException.ThrowIfNull(memoryTypeByQuestionId);

        var flips = new List<LongMemEvalQuestionFlip>();
        var compared = 0;

        foreach (var (questionId, correctWith) in withCapability)
        {
            if (!withoutCapability.TryGetValue(questionId, out var correctWithout)) continue;
            compared++;
            if (correctWith == correctWithout) continue;

            flips.Add(new LongMemEvalQuestionFlip(
                questionId,
                memoryTypeByQuestionId.TryGetValue(questionId, out var type)
                    ? type
                    : LongMemEvalMemoryTypeMap.Unmapped,
                correctWith,
                correctWithout));
        }

        return new LongMemEvalAblationResult(
            capability,
            flips.OrderBy(f => f.QuestionId, StringComparer.Ordinal).ToArray(),
            compared);
    }

    /// <summary>
    /// Whether the movement survives the type's own measured spread.
    /// </summary>
    /// <remarks>
    /// Reported rather than applied: an ablation whose effect is inside the noise floor is a null
    /// result, and a null result is publishable — it is how a component that does not earn its tokens
    /// gets found in-house rather than by a competitor.
    /// </remarks>
    internal static bool SurvivesNoiseFloor(
        LongMemEvalAblationResult result,
        IReadOnlyList<LongMemEvalTypedNoiseFloor> noiseFloors)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(noiseFloors);

        var floor = noiseFloors.FirstOrDefault(f => f.MemoryType == result.Capability.MemoryType);
        return floor is not null && floor.Separates(Math.Abs(result.NetPoints));
    }
}
