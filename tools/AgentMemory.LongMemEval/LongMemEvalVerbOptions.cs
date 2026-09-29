namespace AgentMemory.LongMemEval;

/// <summary>
/// The options each self-parsing verb accepts, for the verbs that never validated their own command
/// line.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a table here and not a list in each program.</b> <c>Program</c> dispatches these verbs before
/// its own validator runs, and each one reads the names it knows and ignores everything else, which
/// is how a flag vanishes without a trace. Five verbs (the plain verb, prepared-pair, reference-arm,
/// typedmemeval with its re-grade, and cell-probe) own a <c>KnownOptions</c> list and validate against
/// it. The other sixteen validated nothing. Their parsers are each a handful of <c>Value(args, …)</c>
/// reads, so one table in one place is less machinery than sixteen lists.
/// </para>
/// <para>
/// <b>Kept honest by a source test.</b> <c>VerbOptionTableTests</c> scans each verb's file for every
/// <c>"--…"</c> literal and requires it here. A verb that learns a new option without this table
/// learning it too fails the build's tests instead of rejecting a valid command at run time.
/// </para>
/// </remarks>
internal static class LongMemEvalVerbOptions
{
    /// <summary>
    /// Verb switch, the file that parses it (null when <c>Program</c> handles it inline), and every
    /// option that file reads.
    /// </summary>
    internal static readonly IReadOnlyDictionary<string, (string? SourceFile, string[] Options)> Verbs =
        new Dictionary<string, (string?, string[])>(StringComparer.Ordinal)
        {
            ["--surface-probe"] = ("LongMemEvalSurfaceProbeProgram.cs",
                ["--volume", "--output"]),
            ["--predicate-distribution"] = ("LongMemEvalPredicateDistributionProgram.cs",
                ["--volume", "--output", "--seed", "--seeds", "--held-out-fraction"]),
            ["--extraction-compare"] = ("LongMemEvalExtractionCompareProgram.cs",
                ["--arm", "--dataset", "--extraction-seed", "--output", "--repeat", "--seed", "--turns",
                 "--units", "--use-predicate-vocabulary", "--vocabulary-ab"]),
            ["--list-prepared-corpora"] = (null, []),
            ["--capture-headroom"] = ("LongMemEvalCaptureHeadroomProgram.cs",
                ["--artifacts"]),
            ["--oracle-representation"] = ("LongMemEvalRepresentationProgram.cs",
                ["--dataset", "--output", "--questions", "--seed"]),
            ["--oracle-precision"] = ("LongMemEvalContextPrecisionProgram.cs",
                ["--dataset", "--distractor-sessions", "--gold-fraction", "--output", "--question-ids",
                 "--questions", "--seed"]),
            ["--oracle-decomposition"] = ("LongMemEvalOracleDecompositionProgram.cs",
                ["--dataset", "--max-sub-questions", "--no-content", "--output", "--question-ids",
                 "--questions", "--seed"]),
            ["--typed-report"] = ("LongMemEvalTypedReportProgram.cs",
                ["--arm", "--output", "--reports"]),
            ["--probe-answer-determinism"] = ("LongMemEvalAnswerDeterminismProgram.cs",
                ["--artifacts", "--dataset", "--include-text", "--probe-questions", "--question-ids",
                 "--questions", "--repeats", "--seed"]),
            ["--upstream-oracle"] = ("LongMemEvalUpstreamOracleProgram.cs",
                ["--artifacts", "--dataset", "--distractor-sessions", "--gold-fraction", "--questions",
                 "--seed"]),
            ["--time-grounded-oracle"] = ("LongMemEvalTimeGroundedOracleProgram.cs",
                ["--artifacts"]),
            ["--procedure-retrieval"] = ("ProcedureRetrievalProgram.cs",
                ["--artifacts", "--min-scores"]),
            ["--procedural-benefit"] = ("ProceduralBenefitProgram.cs",
                ["--attempts", "--task"]),
            ["--scoreboard"] = ("TypedMemEvalScoreboardProgram.cs",
                ["--arm", "--artifacts"]),
            // Positional report paths follow the switch; they do not begin with "--", so the
            // validator leaves them alone.
            ["--census"] = ("TypedMemEvalCensusProgram.cs", []),
        };

    /// <summary>
    /// Validates <paramref name="args"/> for <paramref name="verb"/>, printing the refusal.
    /// </summary>
    /// <returns>Null when the command line is valid; otherwise the exit code to return.</returns>
    /// <remarks>
    /// Returns rather than throws, because the dispatch site has no handler of its own. An unhandled
    /// exception there would print a stack trace instead of the one line that names the flag.
    /// </remarks>
    internal static int? Reject(string[] args, string verb, TextWriter? error = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var (_, options) = Verbs[verb];
        try
        {
            LongMemEvalArgumentValidator.Validate(args, [verb, .. options]);
            return null;
        }
        catch (ArgumentException exception)
        {
            (error ?? Console.Error).WriteLine($"longmemeval: {exception.Message}");
            return 1;
        }
    }
}
