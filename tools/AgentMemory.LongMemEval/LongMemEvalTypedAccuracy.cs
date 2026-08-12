namespace AgentMemory.LongMemEval;

/// <summary>Accuracy and failure attribution for one memory type.</summary>
/// <param name="MemoryType">The type, per the embedded mapping.</param>
/// <param name="Questions">Questions exercising this type.</param>
/// <param name="Correct">How many were judged correct.</param>
/// <param name="ExtractionFailures">
/// Judged wrong AND the gate found the answer nowhere in memory — no retrieval change can help these.
/// </param>
/// <param name="RetrievalFailures">
/// Judged wrong while the answer WAS in memory. The answer exists and did not reach the reader.
/// </param>
/// <param name="Unattributable">
/// Judged wrong but the gate could not check — e.g. a purely numeric gold answer, which is derived
/// rather than stored. Counted apart so "we cannot tell" never reads as "it is missing".
/// </param>
internal sealed record LongMemEvalTypedAccuracy(
    string MemoryType,
    int Questions,
    int Correct,
    int ExtractionFailures,
    int RetrievalFailures,
    int Unattributable)
{
    /// <summary>Accuracy over this type, or null when it has no questions.</summary>
    public double? Accuracy => Questions == 0 ? null : (double)Correct / Questions;

    /// <summary>
    /// Half-width of the band a single question represents, in accuracy points.
    /// </summary>
    /// <remarks>
    /// Emitted beside every per-type figure on purpose. A per-type subset is small — six questions is
    /// 16.7 points each — and a bare percentage over six questions invites exactly the comparison it
    /// cannot support. This is the crudest possible honesty check, not a confidence interval; the
    /// measured per-type noise floor is a separate and larger piece of work.
    /// </remarks>
    public double? PointsPerQuestion => Questions == 0 ? null : 100.0 / Questions;
}

/// <summary>
/// Groups a run's questions by memory type rather than by task label.
/// </summary>
/// <remarks>
/// <para>
/// Every number this project has published is a whole-system number over a dataset that is
/// overwhelmingly semantic, so nothing could say which memory type earned or lost a point. Neither
/// can anyone else: <b>no vendor in the surveyed field publishes a per-memory-type ablation.</b>
/// </para>
/// <para>
/// This is the retrospective half — it runs over results already in hand, needs no model call and no
/// rebuild, and is therefore the cheapest possible first step.
/// </para>
/// </remarks>
internal static class LongMemEvalTypedBreakdown
{
    /// <summary>One row per memory type, ordered by question count.</summary>
    internal static IReadOnlyList<LongMemEvalTypedAccuracy> Summarise(
        IReadOnlyList<LongMemEvalQuestionTelemetry> telemetry,
        IReadOnlyDictionary<string, bool> correctByQuestionId,
        IReadOnlyDictionary<string, bool>? abstentionByQuestionId = null,
        LongMemEvalMemoryTypeMap? map = null)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(correctByQuestionId);
        map ??= LongMemEvalMemoryTypeMap.Default;

        var accumulators = new Dictionary<string, int[]>(StringComparer.Ordinal);

        foreach (var item in telemetry)
        {
            if (item.QuestionId is not { Length: > 0 } id) continue;
            if (!correctByQuestionId.TryGetValue(id, out var correct)) continue;

            var isAbstention = abstentionByQuestionId is not null
                && abstentionByQuestionId.TryGetValue(id, out var abs) && abs;

            // A question mapped to several types counts once under EACH: the types are not a partition,
            // and forcing one would silently discard the second reading rather than report both.
            foreach (var type in map.ForQuestion(item.QuestionType, isAbstention))
            {
                if (!accumulators.TryGetValue(type, out var slot))
                    accumulators[type] = slot = new int[5];

                slot[0]++;                                   // questions
                if (correct) { slot[1]++; continue; }        // correct

                var presence = item.AnswerPresence;
                if (presence is null || !presence.Checkable) slot[4]++;      // unattributable
                else if (presence.Present) slot[3]++;                        // retrieval-side
                else slot[2]++;                                              // extraction-side
            }
        }

        return accumulators
            .Select(pair => new LongMemEvalTypedAccuracy(
                pair.Key, pair.Value[0], pair.Value[1], pair.Value[2], pair.Value[3], pair.Value[4]))
            .OrderByDescending(row => row.Questions)
            .ThenBy(row => row.MemoryType, StringComparer.Ordinal)
            .ToArray();
    }
}
