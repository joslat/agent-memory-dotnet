namespace AgentMemory.LongMemEval;

/// <summary>
/// G4-REF. A reference arm that deliberately uses <b>no</b> AgentMemory at all, so that an
/// AgentMemory score has something to be measured against.
/// </summary>
/// <remarks>
/// Every LongMemEval number accepted before these arms existed compared one AgentMemory
/// configuration against another, which cannot answer the question a prospective adopter asks first:
/// does this beat simply handing the model the chat history? The floor and the ceiling bracket that
/// question. Neither arm stores, extracts, embeds, or recalls anything.
/// </remarks>
public enum LongMemEvalReferenceArm
{
    /// <summary>The question alone — the model's pure parametric floor with no context whatsoever.</summary>
    NoMemory,

    /// <summary>
    /// Every real source turn in the conversation, in order, in the answer model's context — the
    /// ceiling that retrieval is trying to approach.
    /// </summary>
    FullHistory
}

internal static class LongMemEvalReferenceArmExtensions
{
    /// <summary>
    /// Identity recorded in the report. Deliberately prefixed so it can never be mistaken for one of
    /// the three <see cref="LongMemEvalMemoryMode"/> fingerprints in a ledger or a comparison.
    /// </summary>
    public static string Fingerprint(this LongMemEvalReferenceArm arm) => arm switch
    {
        LongMemEvalReferenceArm.NoMemory => "reference-no-memory",
        // The de-contamination is part of the arm's definition, not an option: AgentEval's formatter
        // boilerplate is an artifact of the harness, not conversation, and G3B.1 measured it at 80%
        // of the recalled context. A contaminated ceiling would understate itself.
        LongMemEvalReferenceArm.FullHistory => "reference-full-history-decontaminated",
        _ => throw new ArgumentOutOfRangeException(nameof(arm), arm, null)
    };

    /// <summary>
    /// The system prompt for the arm, recorded verbatim in the report.
    /// </summary>
    /// <remarks>
    /// These necessarily differ from the shipped memory prompt, which instructs the model to answer
    /// "using only the retrieved memory below". With no memory block that instruction manufactures
    /// abstentions and would understate the floor, so a neutral variant is used instead. The
    /// anti-hallucination clause is preserved in both, and the difference is a stated limitation of
    /// the comparison rather than a hidden one.
    /// </remarks>
    public static string SystemPrompt(this LongMemEvalReferenceArm arm) => arm switch
    {
        LongMemEvalReferenceArm.NoMemory =>
            "Answer the question. Be concise and do not claim information you do not have.",
        LongMemEvalReferenceArm.FullHistory =>
            "Answer the question using only the conversation history below. " +
            "Be concise and do not claim information that is absent from the history.",
        _ => throw new ArgumentOutOfRangeException(nameof(arm), arm, null)
    };

    public static bool UsesHistory(this LongMemEvalReferenceArm arm) =>
        arm is LongMemEvalReferenceArm.FullHistory;
}

/// <summary>Per-question accounting for a reference arm. Content-free by construction.</summary>
public sealed record LongMemEvalReferenceTelemetry(
    int QuestionNumber,
    string? QuestionId,
    string Status,
    int HistoryTurnsProvided,
    int SyntheticTurnsDropped,
    int PromptCharacters,
    int EstimatedPromptTokens);

/// <summary>
/// Which of a question's injected messages are AgentEval formatter artifacts rather than real
/// conversation. Abstracted so the arm can be tested without loading the 264 MB dataset.
/// </summary>
internal interface ILongMemEvalReferenceOriginResolver
{
    LongMemEvalReferenceOrigins Resolve(
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        string prompt);
}

/// <summary>
/// <paramref name="IsSynthetic"/> is parallel to the flattened injected message list: two entries per
/// history turn, user first.
/// </summary>
internal sealed record LongMemEvalReferenceOrigins(
    string QuestionId,
    IReadOnlyList<bool> IsSynthetic);

/// <summary>Resolves origins through the real evaluator-side evidence index.</summary>
internal sealed class LongMemEvalEvidenceOriginResolver(LongMemEvalEvidenceIndex index)
    : ILongMemEvalReferenceOriginResolver
{
    public LongMemEvalReferenceOrigins Resolve(
        IReadOnlyList<(string UserMessage, string AssistantResponse)> history,
        string prompt)
    {
        var question = index.Resolve(history, prompt);
        var expected = history.Count * 2;
        if (question.Messages.Count != expected)
        {
            throw new InvalidOperationException(
                $"LongMemEval evidence contained {question.Messages.Count} origins for {expected} injected messages.");
        }

        return new LongMemEvalReferenceOrigins(
            question.QuestionId,
            question.Messages
                .Select(origin => origin.IsSyntheticBoundary || origin.IsSyntheticFormatterPadding)
                .ToArray());
    }
}
