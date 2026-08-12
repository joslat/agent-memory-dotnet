using System.Globalization;
using System.Text.RegularExpressions;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Whether a provider's content refusal cost a question its <b>answer</b>, or merely some context.
/// </summary>
/// <remarks>
/// <para>
/// A refused source session is skipped so the build survives, and 3 of 2,386 sessions is well inside
/// tolerance. But "inside tolerance" is a statement about volume, not about consequence: if the
/// refused session is the one holding a question's gold evidence, that question becomes
/// <b>unanswerable from memory</b> and will score as wrong — attributing an Azure content-policy
/// decision to our recall quality.
/// </para>
/// <para>
/// That is the exact failure this track exists to prevent: a number that is internally consistent,
/// reproducible, and measuring something other than what it claims. Recording <i>that</i> sessions
/// were refused is not enough; what matters is whether they <i>mattered</i>.
/// </para>
/// <para>
/// This does not change the score. Excluding a question from accuracy would require reaching into
/// AgentEval's scoring, and silently dropping questions is its own way to make a metric lie. It
/// raises a validation issue instead, so an affected run is flagged rather than quietly accepted, and
/// the reader can decide.
/// </para>
/// </remarks>
internal static class LongMemEvalRefusedEvidence
{
    // {preparationId}-session-{questionIndex:D4}-source-{sourceSessionOrdinal:D4}
    private static readonly Regex SessionIdPattern = new(
        @"-session-(?<question>\d{4})-source-(?<source>\d{4})$",
        RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture, TimeSpan.FromSeconds(1));

    /// <summary>One refused session, and what it cost.</summary>
    /// <param name="SessionId">The scoped session id the provider refused.</param>
    /// <param name="QuestionId">The dataset question whose history it belonged to, when resolvable.</param>
    /// <param name="HeldGoldEvidence">
    /// Whether that source session contained a message the dataset marks as answer-bearing. True means
    /// the question can no longer be answered from memory <b>for a reason unrelated to retrieval</b>.
    /// </param>
    internal readonly record struct RefusedSession(
        string SessionId, string? QuestionId, bool HeldGoldEvidence);

    /// <summary>
    /// Resolves each refused session against the sampled questions' gold evidence.
    /// </summary>
    /// <remarks>
    /// A session id that does not parse, or names a question outside the sample, yields
    /// <c>HeldGoldEvidence = false</c> with a null question — unknown rather than assumed harmless,
    /// and visible as such because the id is still reported.
    /// </remarks>
    internal static IReadOnlyList<RefusedSession> Analyse(
        IReadOnlyList<string> refusedSessionIds,
        IReadOnlyList<LongMemEvalEvidenceQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(refusedSessionIds);
        ArgumentNullException.ThrowIfNull(questions);

        var analysed = new List<RefusedSession>(refusedSessionIds.Count);
        foreach (var sessionId in refusedSessionIds)
        {
            var match = SessionIdPattern.Match(sessionId);
            if (!match.Success)
            {
                analysed.Add(new RefusedSession(sessionId, null, false));
                continue;
            }

            // The question index is 1-based in the id, matching how preparation numbers them.
            var questionIndex = int.Parse(
                match.Groups["question"].Value, CultureInfo.InvariantCulture) - 1;
            var sourceOrdinal = int.Parse(
                match.Groups["source"].Value, CultureInfo.InvariantCulture);

            if (questionIndex < 0 || questionIndex >= questions.Count)
            {
                analysed.Add(new RefusedSession(sessionId, null, false));
                continue;
            }

            var question = questions[questionIndex];
            var heldGold = question.Messages.Any(message =>
                message.SourceSessionOrdinal == sourceOrdinal
                && message.HasAnswer
                && !message.IsSyntheticBoundary
                && !message.IsSyntheticFormatterPadding);

            analysed.Add(new RefusedSession(sessionId, question.QuestionId, heldGold));
        }

        return analysed;
    }

    /// <summary>
    /// The validation issue for a run whose refusals cost gold evidence, or null when none did.
    /// </summary>
    /// <remarks>
    /// Phrased around the consequence rather than the count, because the count is already reported and
    /// the consequence is what changes how the accuracy number should be read.
    /// </remarks>
    internal static string? DescribeCompromisedQuestions(IReadOnlyList<RefusedSession> analysed)
    {
        ArgumentNullException.ThrowIfNull(analysed);

        var compromised = analysed
            .Where(session => session.HeldGoldEvidence && session.QuestionId is not null)
            .Select(session => session.QuestionId!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToArray();

        return compromised.Length == 0
            ? null
            : $"The provider refused source sessions holding the gold evidence for "
              + $"{compromised.Length} question(s): {string.Join(", ", compromised)}. Those questions "
              + "are unanswerable from this corpus for a reason unrelated to retrieval, so counting "
              + "them as wrong attributes a content-policy decision to recall quality.";
    }
}
