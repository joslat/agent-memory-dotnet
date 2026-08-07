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
    /// <summary>
    /// The question alone.
    /// </summary>
    /// <remarks>
    /// This is not a degenerate configuration — it is the realistic one. In LongMemEval the question
    /// arrives in a <b>fresh session</b>, and an ordinary agent carries no chat history across
    /// sessions, so "nothing" is exactly what an agent without a memory layer has. The gap between
    /// this arm and AgentMemory is therefore the product's actual value, not a strawman.
    /// </remarks>
    NoMemory,

    /// <summary>
    /// Every real message in the conversation, in order, in the answer model's context.
    /// </summary>
    /// <remarks>
    /// Deliberately <b>not</b> called a ceiling. It is a competing <i>strategy</i> — "no memory
    /// layer, replay the entire transcript into every prompt" — not an upper bound, and a memory
    /// system that distils better context could in principle beat it. It is also only available
    /// while the transcript still fits the window, which is a property of the dataset rather than of
    /// the strategy.
    /// </remarks>
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
        // The de-contamination is part of every history arm's definition, not an option: AgentEval's
        // formatter boilerplate is an artifact of the harness, not conversation, and G3B.1 measured
        // it at 80% of the recalled context. A contaminated baseline would understate itself.
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
/// <remarks>
/// Counts are <b>messages</b>, not conversation turns — two messages per turn — because AgentMemory's
/// own <c>MaxRelevantMessages</c> budget is denominated in messages, and the equal-budget comparison
/// is only exact if both sides count the same unit.
/// </remarks>
public sealed record LongMemEvalReferenceTelemetry(
    int QuestionNumber,
    string? QuestionId,
    string Status,
    int HistoryMessagesProvided,
    int SyntheticMessagesDropped,
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
/// <paramref name="IsSynthetic"/> and <paramref name="SourceTimestamps"/> are parallel to the
/// flattened injected message list: two entries per history turn, user first.
/// </summary>
/// <remarks>
/// G3B.2 carries the timestamps because the session dates otherwise exist only in the boundary
/// markers this arm drops. The baseline is fixed in the same change as the memory arm, so the
/// comparison measures the memory system rather than which side received the fix.
/// </remarks>
internal sealed record LongMemEvalReferenceOrigins(
    string QuestionId,
    IReadOnlyList<bool> IsSynthetic,
    IReadOnlyList<string> SourceTimestamps,
    string? QuestionDate);

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
                .ToArray(),
            question.Messages.Select(origin => origin.SourceTimestamp).ToArray(),
            question.QuestionDate);
    }
}
