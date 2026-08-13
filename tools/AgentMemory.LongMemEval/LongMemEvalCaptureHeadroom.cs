namespace AgentMemory.LongMemEval;

/// <summary>One question's outcome, reduced to what decides whether more capture could help it.</summary>
/// <param name="QuestionId">The question.</param>
/// <param name="MemoryType">Its memory type, from the taxonomy mapping.</param>
/// <param name="Correct">Whether the judge scored it correct.</param>
/// <param name="AnswerCheckable">
/// Whether the presence gate could be evaluated at all. A gold answer with no distinctive tokens is
/// uncheckable, and reporting it either way would be an invention.
/// </param>
/// <param name="AnswerPresent">Whether the gold answer's distinctive tokens were in the assembled context.</param>
public sealed record CaptureHeadroomQuestion(
    string QuestionId,
    string MemoryType,
    bool Correct,
    bool AnswerCheckable,
    bool AnswerPresent);

/// <summary>
/// How much of one memory type's failure is reachable by capturing <i>more</i>, per type.
/// </summary>
/// <param name="MemoryType">The type this row describes.</param>
/// <param name="Questions">Questions of this type.</param>
/// <param name="Failures">Of those, how many the judge scored wrong.</param>
/// <param name="FailuresCheckable">Failures where the presence gate could be evaluated.</param>
/// <param name="FailuresAnswerPresent">
/// Failures where the answer was <b>already in the context</b> and the run was wrong anyway. More
/// capture cannot fix these — nothing was missing.
/// </param>
public sealed record CaptureHeadroomType(
    string MemoryType,
    int Questions,
    int Failures,
    int FailuresCheckable,
    int FailuresAnswerPresent)
{
    /// <summary>
    /// Failures where the answer was checkable and <b>absent</b> — the only ones a capture-side change
    /// could possibly convert.
    /// </summary>
    public int CaptureReachableFailures => FailuresCheckable - FailuresAnswerPresent;

    /// <summary>
    /// The most a capture-side change could add to this type's accuracy, as a fraction, on this sample.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A ceiling, not an estimate.</b> It assumes every absent answer would be captured by the change
    /// under test <i>and</i> that capturing it converts the question — both generous. The useful direction
    /// is downward: when the ceiling is at or below one question, no run of this size can produce a gain
    /// that clears the type's own noise band, and the expensive arm comparison is decided before it is
    /// paid for.
    /// </para>
    /// <para>
    /// Uncheckable failures are excluded from the numerator rather than assumed reachable. Counting them
    /// as headroom would inflate exactly the number used to justify spending.
    /// </para>
    /// </remarks>
    public double CaptureCeiling => Questions == 0 ? 0d : (double)CaptureReachableFailures / Questions;

    /// <summary>Accuracy on this sample, for context alongside the ceiling.</summary>
    public double Accuracy => Questions == 0 ? 0d : (double)(Questions - Failures) / Questions;
}

/// <summary>
/// Splits each memory type's failures into "nothing was stored" and "it was stored and still missed"
/// (PLAN 8.3c).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> 8.3b asks whether the episodic capture mode should ship on, and the plan
/// costs the answer at roughly 96M input tokens — ~30 questions × 3 cold builds × 2 arms, because
/// extraction is nondeterministic and a single build proves nothing. That is a real bill, and it is
/// worth asking first what the run could possibly show.
/// </para>
/// <para>
/// <c>AssistantContentMode</c> is a <b>capture</b> setting: it stores more. So it can only convert a
/// failure where something needed was <i>never stored</i>. A failure whose gold answer was already
/// sitting in the assembled context is a retrieval, ranking or answering failure, and storing more
/// cannot fix it — it makes the context bigger, which the measured cost (32.3% of the retrieval budget,
/// +23.1% prompt tokens) says is not free.
/// </para>
/// <para>
/// The answer-presence gate already computes the needed signal on every recorded question, so this runs
/// over artifacts on disk: <b>zero model calls, zero rebuilds</b>. It is the same move Phase 0 made, and
/// it is the cheapest rung of an expensive decision.
/// </para>
/// <para>
/// <b>What it is not.</b> Presence is a token-overlap gate, not a proof of sufficiency: the gold answer's
/// distinctive tokens can appear in the context without the context actually supporting the answer. That
/// makes <see cref="CaptureHeadroomType.CaptureCeiling"/> a <b>ceiling on a weak signal</b> — sound for
/// arguing a run cannot help, unsound for arguing one would. It also cannot distinguish "absent because
/// the assistant's act was never captured" from "absent for some other reason", so a non-zero ceiling
/// is an upper bound on the specific mode under test, never a forecast of it.
/// </para>
/// </remarks>
public static class LongMemEvalCaptureHeadroom
{
    /// <summary>
    /// Groups questions by memory type and reports each type's capture-reachable failures.
    /// </summary>
    /// <remarks>
    /// Types are keyed by the caller-supplied memory type, not by the dataset's task label: a task
    /// taxonomy is not a memory-type taxonomy, and conflating them is the substitution the taxonomy file
    /// exists to prevent.
    /// </remarks>
    public static IReadOnlyList<CaptureHeadroomType> Summarise(
        IEnumerable<CaptureHeadroomQuestion> questions)
    {
        ArgumentNullException.ThrowIfNull(questions);

        return questions
            .GroupBy(question => question.MemoryType, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group =>
            {
                var failures = group.Where(question => !question.Correct).ToList();
                return new CaptureHeadroomType(
                    MemoryType: group.Key,
                    Questions: group.Count(),
                    Failures: failures.Count,
                    FailuresCheckable: failures.Count(question => question.AnswerCheckable),
                    FailuresAnswerPresent: failures.Count(question =>
                        question.AnswerCheckable && question.AnswerPresent));
            })
            .ToList();
    }

    /// <summary>
    /// Whether a capture-side change is worth measuring for this type at this sample size.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The decision rule for 8.3b requires a gain that exceeds the type's own noise band across ≥3 builds
    /// per arm. A gain smaller than <b>one question</b> cannot clear any noise band, because accuracy on
    /// an n-question sample moves in steps of 1/n — so a ceiling below one whole question means the rule
    /// returns "leave it opt-in" whatever the run reports.
    /// </para>
    /// <para>
    /// Deliberately expressed in <i>questions</i> rather than percentage points. A percentage threshold
    /// looks scale-free and hides the quantisation that actually decides the outcome: at n=4, one
    /// question is 25 points.
    /// </para>
    /// </remarks>
    public static bool WorthMeasuring(CaptureHeadroomType type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.CaptureReachableFailures >= 1;
    }
}
