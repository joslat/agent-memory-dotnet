using System.Globalization;
using AgentEval.Evals.Meta;
using AgentEval.Memory.External.Models;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Reports each shape's score against a DECLARED chance floor, using AgentEval 0.35's floor
/// calculus — so "we scored 55%" can be read as "and a coin scores 50%".
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists.</b> Temporal's <c>occurrence-order</c> cell read 11/20 = 55% against a chance
/// floor of 0.50: within noise of a coin, and nothing in the artifact said so. The floor had to be
/// remembered from correspondence, which means a cell could be — and was nearly — reported as a
/// result when it carried no information.
/// </para>
/// <para>
/// <b>Floors are DERIVED from a shape's construction, never guessed.</b> A binary "which came first"
/// question is <see cref="ChanceFloor.UniformChoice"/> with two alternatives. Where the answer space
/// is not something we can establish from the corpus, the floor is
/// <see cref="ChanceFloor.NotDerivable"/> with the reason — <b>not zero</b>. A zero floor reads as
/// "any score beats chance", which is exactly the false confidence this closes, and AgentEval built
/// that distinction into the type rather than leaving it to callers.
/// </para>
/// <para>
/// <b>The census matters as much as the floor.</b> <c>ObservationCensus</c> separates Measured from
/// NotMeasured, and a run whose questions failed for infrastructure reasons is <c>Void</c> rather
/// than low-scoring. Two paid runs in this project died mid-flight and reported 7/39; mapping agent
/// failures to <see cref="MeasurementState.NotMeasured"/> makes that structural instead of something
/// a reader has to notice.
/// </para>
/// </remarks>
internal static class TypedMemEvalFloorReport
{
    /// <summary>
    /// The floor a shape's construction implies, or <c>NotDerivable</c> with the reason.
    /// </summary>
    /// <remarks>
    /// Deliberately short. Only shapes whose answer space is actually known are declared; everything
    /// else says so. Guessing an alternatives count to make a cell comparable would manufacture the
    /// comparison rather than earn it — and a wrong floor is worse than no floor, because it converts
    /// "uninformative" into "beats chance".
    /// </remarks>
    internal static ChanceFloor FloorFor(string shape) => shape switch
    {
        // A "which of these two came first" question is a coin, and AgentEval publish 0.50 for it.
        // UniformChoice(2) DERIVES that number rather than quoting it, so the artifact carries the
        // reasoning and not just the constant.
        "occurrence-order" => ChanceFloor.UniformChoice(2),

        // Everything else: the corpus does not declare the answer space, and neither can we. A
        // count question ("how many deliveries") has an unbounded integer answer space; a sum has a
        // continuous one; naming an alternatives count for either would be invention.
        _ => ChanceFloor.NotDerivable(
            $"the answer space of '{shape}' is not declared by the corpus and cannot be derived "
            + "from its construction; a guessed floor would convert an uninformative cell into one "
            + "that appears to beat chance"),
    };

    /// <summary>
    /// One <see cref="Observation"/> per question, with infrastructure failures marked NOT MEASURED.
    /// </summary>
    /// <remarks>
    /// The mapping is the whole point of using their type. An agent failure is not a wrong answer —
    /// it is an absent measurement, and pooling the two is how a dead run reports a score. Abstention
    /// IS a measured outcome (the engine was asked and declined), so it counts as an incorrect
    /// answer rather than a missing one.
    /// </remarks>
    internal static IReadOnlyList<Observation> Observations(
        IEnumerable<QuestionResult> questions, string armId)
    {
        ArgumentNullException.ThrowIfNull(questions);

        return questions.Select(question => new Observation(
                CaseId: question.QuestionId ?? "(unidentified)",
                ArmId: armId,
                // Correct is nullable: null means the judge never returned a verdict, which is an
                // absent measurement rather than a wrong answer -- the same distinction the State
                // below carries, and conflating them would let an ungraded question score zero.
                Value: question.Correct == true ? 1.0 : 0.0,
                State: question.ExecutionStatus == QuestionExecutionStatus.Completed
                       && question.Correct is not null
                    ? MeasurementState.Measured
                    : MeasurementState.NotMeasured))
            .ToArray();
    }

    /// <summary>Prints one line per shape: the score, its floor, and whether it clears it.</summary>
    internal static void Print(ExternalBenchmarkResult result, string armId)
    {
        ArgumentNullException.ThrowIfNull(result);

        var byShape = result.QuestionResults
            .GroupBy(question => question.TypedOutcome?.Shape ?? "(unshaped)")
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in byShape)
        {
            var floor = FloorFor(group.Key);
            var observations = Observations(group, armId);
            var comparison = FloorComparison.Compute(observations, armId, floor);
            var census = comparison.Census;

            // Void first: a shape whose measurements are missing has no score to discuss, and
            // printing one would be the 7/39 mistake in miniature.
            if (census.Void)
            {
                Console.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"typedmemeval: floor {group.Key} — VOID: {census.Measured} of {census.Total} "
                    + $"measured. No score is reported for this shape."));
                continue;
            }

            var verdict = floor.State == FloorState.NotDerivable
                ? "no floor declared"
                : comparison.AboveFloor
                    ? $"ABOVE floor {comparison.FloorUsed:F3} (p={comparison.PValue:F3})"
                    : $"NOT above floor {comparison.FloorUsed:F3} (p={comparison.PValue:F3})";

            var caveat = comparison.UnderpoweredByConstruction
                ? " ⚠ UNDERPOWERED BY CONSTRUCTION — n cannot support the claim"
                : string.Empty;

            Console.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"typedmemeval: floor {group.Key} — {comparison.Successes}/{comparison.Trials}, "
                + $"{verdict}{caveat}"));
        }
    }
}
