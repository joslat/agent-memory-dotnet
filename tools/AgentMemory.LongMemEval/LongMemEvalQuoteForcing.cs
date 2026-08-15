namespace AgentMemory.LongMemEval;

/// <summary>
/// 30.11, the other half: make the model quote its evidence before answering, then read the answer back
/// out.
/// </summary>
/// <remarks>
/// <para>
/// Quote-forcing and voting compose, which is why they ship together. Voting reduces variance in what
/// the model says; quote-forcing constrains what it is allowed to say by making it name the retrieved
/// line it is answering from first. A model that cannot find a supporting quote is offered an explicit
/// escape — <c>EVIDENCE: NONE FOUND</c> — because the alternative to admitting absence is inventing
/// presence.
/// </para>
/// <para>
/// <b>Off is byte-identical.</b> The system prompt is unchanged unless quote-forcing is on, and the
/// parser is only reached for a response produced under the quote-forcing prompt. Every archived
/// measurement was taken under the plain prompt and stays comparable.
/// </para>
/// </remarks>
internal static class LongMemEvalQuoteForcing
{
    /// <summary>The escape a model uses when memory does not support an answer.</summary>
    internal const string NoneFound = "NONE FOUND";

    /// <summary>
    /// The quote-forcing system prompt: the base instruction plus the required output shape.
    /// </summary>
    /// <remarks>
    /// Built from <paramref name="basePrompt"/> rather than replacing it, so the two instructions cannot
    /// drift apart — the base prompt already carries the "do not claim information absent from memory"
    /// clause that this format exists to make checkable.
    /// </remarks>
    public static string SystemPrompt(string basePrompt) =>
        basePrompt
        + " Respond in exactly two lines. First line: EVIDENCE: \"<a verbatim quote from the retrieved "
        + "memory that supports your answer>\" — or EVIDENCE: " + NoneFound + " if the memory does not "
        + "support one. Second line: ANSWER: <your answer>. Do not add anything else.";

    /// <summary>
    /// Extracts the answer from a quote-forced response.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Falls back to the whole response when the format is absent.</b> A model that ignored the
    /// format still answered, and discarding that answer would convert a formatting miss into a scored
    /// failure — measuring instruction-following where the run is measuring memory. The fallback is
    /// reported (<see cref="QuoteForcedAnswer.FormatHonoured"/>) so the rate is visible rather than
    /// absorbed.
    /// </para>
    /// <para>
    /// The evidence line is returned but never scored. It exists so a reader can see what the model
    /// believed it was answering from — and, when the answer is wrong, whether the quote was wrong too
    /// or whether the model had the right line and drew the wrong conclusion. Those are different
    /// defects with different fixes, and they are indistinguishable from the answer alone.
    /// </para>
    /// </remarks>
    public static QuoteForcedAnswer Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
            return new QuoteForcedAnswer { Answer = string.Empty, FormatHonoured = false };

        string? evidence = null;
        string? answer = null;

        foreach (var rawLine in response.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("EVIDENCE:", StringComparison.OrdinalIgnoreCase))
                evidence = line["EVIDENCE:".Length..].Trim().Trim('"').Trim();
            else if (line.StartsWith("ANSWER:", StringComparison.OrdinalIgnoreCase))
                answer = line["ANSWER:".Length..].Trim();
        }

        if (answer is null)
            return new QuoteForcedAnswer { Answer = response.Trim(), FormatHonoured = false };

        return new QuoteForcedAnswer
        {
            Answer = answer,
            Evidence = evidence,
            // NONE FOUND is the model saying memory does not support an answer. Recorded distinctly from
            // "no evidence line at all", because one is an admission and the other is a formatting miss.
            EvidenceAbsent = string.Equals(evidence, NoneFound, StringComparison.OrdinalIgnoreCase),
            FormatHonoured = true,
        };
    }
}

/// <summary>An answer, and the quote the model said it came from.</summary>
internal sealed record QuoteForcedAnswer
{
    /// <summary>The answer text, or the whole response when the format was not honoured.</summary>
    public required string Answer { get; init; }

    /// <summary>The verbatim quote the model cited, if any.</summary>
    public string? Evidence { get; init; }

    /// <summary>True when the model explicitly reported that memory supports no answer.</summary>
    public bool EvidenceAbsent { get; init; }

    /// <summary>
    /// False when the response did not use the format. Reported, not corrected.
    /// </summary>
    /// <remarks>
    /// A high rate here means the run is measuring instruction-following rather than memory, and the
    /// numbers should be read with that in mind — which is only possible if the rate is on the artifact.
    /// </remarks>
    public required bool FormatHonoured { get; init; }
}
