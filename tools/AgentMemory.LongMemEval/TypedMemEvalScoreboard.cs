using System.Globalization;
using System.Text.Json;
using AgentEval.Memory.External.TypedMemEval;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Assembles the ten-vertical C-D scoreboard from stored artifacts — three columns, and a refusal
/// wherever a cell cannot honestly carry a number.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an assembler rather than a table typed by hand.</b> Ten verticals land over days, from
/// separate runs, and the only thing that makes two of them comparable is that they share a corpus
/// lineage, a judge and an arm. A hand-built table records the numbers and drops exactly the fields
/// that decide whether the numbers may sit beside each other. This reads them back out of the
/// artifacts and <b>refuses to assemble</b> when they disagree.
/// </para>
/// <para>
/// <b>The three columns, and why none of them is enough alone.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>share-of-all</b> — correct over scored. The number every previous artifact reported, and the
/// one that silently assumes a perfect engine could score 1.0.
/// </description></item>
/// <item><description>
/// <b>share-of-reachable</b> — correct over the corpus's own declared ceiling at <c>k_ref</c>. Three
/// of ten corpora cap below 1.0, so this is the denominator a gap should be read against.
/// </description></item>
/// <item><description>
/// <b>ranking-only</b> — the same score restricted to shapes AgentEval predict can separate systems
/// under a <i>dense</i> retriever, which is what this stack uses. 8 of 35 shapes cannot, and a cell
/// resting on them looks like evidence while carrying none.
/// </description></item>
/// </list>
/// <para>
/// <b>Columns 2 and 3 are deliberately NOT composed.</b> The ceiling is declared per corpus, over all
/// its questions; restricting the numerator to a subset of shapes while keeping a whole-corpus
/// denominator would produce a ratio with no meaning. They answer different questions and are
/// printed side by side rather than multiplied.
/// </para>
/// </remarks>
internal static class TypedMemEvalScoreboard
{
    /// <summary>Reads every placeable run from an artifacts directory and builds the scoreboard.</summary>
    /// <param name="artifactsDirectory">Directory holding the report and provenance sidecars.</param>
    /// <param name="armToken">The arm every placed row must carry — mixing arms is the row-54 error.</param>
    /// <remarks>
    /// <b>Lineage is decided on the corpus sha, never on the package version.</b> Seven of the ten
    /// corpora were byte-identical across 0.34, 0.35 and 0.36, so their runs are valid on the current
    /// pin however they are labelled — and a version gate would have read three finished verticals as
    /// ABSENT and sent them back for an ~11h re-run that would have changed nothing. The sha is
    /// checked against the corpus <i>this build carries</i>, so the table verifies itself against the
    /// package in hand rather than against an argument someone typed.
    /// </remarks>
    internal static Scoreboard Assemble(string artifactsDirectory, string armToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactsDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(armToken);

        var latest = LatestRunPerVertical(artifactsDirectory, armToken);
        var rows = new List<ScoreboardRow>();

        foreach (var descriptor in TypedMemEvalVerticals.All.OrderBy(v => v.Slug, StringComparer.Ordinal))
        {
            rows.Add(latest.TryGetValue(descriptor.Slug, out var run)
                ? Place(descriptor.Slug, run)
                // Never a zero: a vertical that was never run and a vertical that scored nothing look
                // identical in a table of numbers, and they are opposites.
                : ScoreboardRow.NotPlaced(descriptor.Slug, "no run on this arm against this build's corpus"));
        }

        return new Scoreboard(rows, armToken);
    }

    /// <summary>
    /// The newest run per vertical for one arm, keyed by slug.
    /// </summary>
    /// <remarks>
    /// Provenance is the entry point, not the report: the arm lives in the sidecar, and inferring it
    /// from a filename would let a renamed file place a row under the wrong arm. A report without a
    /// sidecar is <b>not placeable</b> — its arm cannot be established, and guessing "default" is how
    /// an ON-arm result ends up on an OFF-arm scoreboard.
    /// </remarks>
    private static Dictionary<string, RunArtifacts> LatestRunPerVertical(string directory, string armToken)
    {
        var newest = new Dictionary<string, RunArtifacts>(StringComparer.Ordinal);
        if (!Directory.Exists(directory)) return newest;

        var buildCorpusSha = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var sidecarPath in Directory.EnumerateFiles(directory, "typedmemeval-*.provenance.json"))
        {
            if (ReadRun(sidecarPath, armToken) is not { } run) continue;

            if (!buildCorpusSha.TryGetValue(run.Vertical, out var current))
            {
                current = TypedMemEvalCorpusSha.For(run.Vertical);
                buildCorpusSha[run.Vertical] = current;
            }

            // A run against a different draw of the corpus is not placeable. It does not refuse the
            // table and it does not fill a row -- it is simply not a measurement of this corpus.
            // Unverifiable counts as not placeable too: an artifact that records no sha cannot be
            // shown to be a measurement of this corpus, and "probably fine" is how a redrawn gold
            // gets ranked against the wrong answers.
            if (TypedMemEvalRegradeProgram.CorpusIdentity.Verify(run.CorpusSha256, current)
                != TypedMemEvalRegradeProgram.CorpusIdentityVerdict.Match)
            {
                continue;
            }

            // A ONE-QUESTION STAGE-2 PROBE IS NOT A ROW. Every full run in this project is preceded
            // by a single-question run that proves the chain end to end, and it lands in the same
            // directory, on the same arm, against the same corpus, minutes before the real one. It is
            // also the NEWEST artifact for hours afterwards. Left in, it would have reported
            // prospective as 0.0% -- a sample of one presented as a vertical.
            if (run.CorpusQuestions > 0 && run.SelectedQuestions < run.CorpusQuestions) continue;

            if (!newest.TryGetValue(run.Vertical, out var existing) || run.StartedUtc > existing.StartedUtc)
            {
                newest[run.Vertical] = run;
            }
        }

        return newest;
    }

    private static RunArtifacts? ReadRun(string sidecarPath, string armToken)
    {
        try
        {
            using var sidecarDocument = JsonDocument.Parse(File.ReadAllBytes(sidecarPath));
            var sidecar = sidecarDocument.RootElement;

            if (!sidecar.TryGetProperty("arm", out var arm) ||
                !arm.TryGetProperty("token", out var token) ||
                token.ValueKind != JsonValueKind.String ||
                !string.Equals(token.GetString(), armToken, StringComparison.Ordinal))
            {
                return null;
            }

            // GetString() throws on any non-string kind, so the kind is checked before the read --
            // a hand-edited or truncated sidecar must make its run unplaceable, not kill the tool.
            if (!sidecar.TryGetProperty("vertical", out var vertical) ||
                vertical.ValueKind != JsonValueKind.String ||
                vertical.GetString() is not { Length: > 0 } slug ||
                !sidecar.TryGetProperty("report", out var report) ||
                report.ValueKind != JsonValueKind.String ||
                report.GetString() is not { Length: > 0 } reportName)
            {
                return null;
            }

            var startedUtc = sidecar.TryGetProperty("startedUtc", out var started) &&
                             started.ValueKind == JsonValueKind.String &&
                             started.TryGetDateTimeOffset(out var value)
                ? value
                : DateTimeOffset.MinValue;

            // Read from the SIDECAR, not the report: the store probe is provenance, and a vertical
            // whose mechanism never fired is a fact about the run rather than about the answers.
            // Absent reads as null -- unknown, never as "it fired".
            int? supersededByEdges =
                sidecar.TryGetProperty("supersessionStore", out var store)
                && store.ValueKind == JsonValueKind.Object
                && store.TryGetProperty("supersededByEdges", out var edges)
                && edges.ValueKind == JsonValueKind.Number
                && edges.TryGetInt32(out var edgeCount)
                    ? edgeCount
                    : null;

            var reportPath = Path.Combine(Path.GetDirectoryName(sidecarPath)!, reportName);
            if (!File.Exists(reportPath)) return null;

            using var reportDocument = JsonDocument.Parse(File.ReadAllBytes(reportPath));
            return Read(slug, startedUtc, supersededByEdges, reportDocument.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            // An unreadable artifact must not take the scoreboard down, and must not be silently
            // counted either: it simply never becomes a placeable run, so its vertical reads ABSENT.
            return null;
        }
    }

    private static RunArtifacts? Read(
        string slug, DateTimeOffset startedUtc, int? supersededByEdges, JsonElement report)
    {
        if (!report.TryGetProperty("TypedOutcomes", out var outcomes) ||
            !report.TryGetProperty("Provenance", out var provenance))
        {
            return null;
        }

        var byShape = new Dictionary<string, ShapeTally>(StringComparer.Ordinal);
        if (outcomes.TryGetProperty("ByShape", out var shapes) && shapes.ValueKind == JsonValueKind.Object)
        {
            foreach (var shape in shapes.EnumerateObject())
            {
                byShape[shape.Name] = new ShapeTally(
                    N: Int(shape.Value, "N"),
                    Correct: Int(shape.Value, "Correct"),
                    Unrun: Int(shape.Value, "Unrun"));
            }
        }

        return new RunArtifacts(
            Vertical: slug,
            StartedUtc: startedUtc,
            CorpusSha256: Str(outcomes, "CorpusSha256"),
            JudgePromptFingerprint: Str(outcomes, "JudgePromptFingerprint"),
            AgentEvalVersion: Str(provenance, "AgentEvalVersion"),
            SelectedQuestions: Int(report, "SelectedQuestions"),
            CorpusQuestions: Int(provenance, "DatasetQuestionCount"),
            ScoredQuestions: Int(report, "ScoredQuestions"),
            CorrectQuestions: Int(report, "CorrectQuestions"),
            AgentFailureQuestions: Int(report, "AgentFailureQuestions"),
            SupersededByEdges: supersededByEdges,
            ByShape: byShape);

        // ValueKind-checked before every Try*: on a JSON null these accessors THROW rather than
        // returning false, and this method's callers treat a malformed artifact as unreadable, not
        // as a reason to take the tool down. A guard that can throw is not a guard.
        static int Int(JsonElement element, string name) =>
            element.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.Number
            && v.TryGetInt32(out var i) ? i : 0;

        static string? Str(JsonElement element, string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
    }

    private static ScoreboardRow Place(string slug, RunArtifacts run)
    {
        // VOID first, before any arithmetic. Two paid runs in this project died mid-flight and
        // reported 7/39 as though it were a score; an agent failure is an absent measurement, not a
        // wrong answer, and a vertical carrying any is not scored at all.
        if (run.AgentFailureQuestions > 0 || run.ScoredQuestions <= 0)
        {
            return ScoreboardRow.Void(slug, run,
                run.AgentFailureQuestions > 0
                    ? $"{run.AgentFailureQuestions} agent failure(s) — an absent measurement, not a wrong answer"
                    : "no scored questions");
        }

        // OFF-STATE, checked before any arithmetic and separately from VOID. A vertical whose
        // shapes measure write-time supersession, run against a store where supersession never
        // wrote a single edge, has measured the ABSENCE of the mechanism. Its questions were asked
        // and answered, so nothing is missing and VOID would be wrong -- but the number is not a
        // measurement of the feature, and placing it beside the others is the one claim this
        // project holds as absolutely forbidden.
        // The test is "can it be SHOWN to have fired", not "is it known to have not fired". An
        // unrecorded probe is unknown, and unknown placed as a number is the same claim as zero
        // placed as a number -- which is why this reads `is > 0` and not `is not 0`.
        if (DependsOnSupersession(slug) && run.SupersededByEdges is not > 0)
        {
            return ScoreboardRow.OffState(slug, run, run.SupersededByEdges is 0
                ? "supersession wrote 0 edges — the questions were answered against a store where the "
                  + "mechanism under test never fired, so this is an off-state, not a score"
                : "this run records no supersession store probe, so the mechanism under test cannot "
                  + "be shown to have fired — unknown is not evidence that it did");
        }

        var ceiling = TypedMemEvalReachableCeiling.For(slug);
        var sensitivity = TypedMemEvalRetrieverSensitivity.For(slug);

        double? shareOfReachable = ceiling is { } c && c.ReachableQuestions > 0
            ? c.ShareOfReachable(run.CorrectQuestions)
            : null;

        var ranking = RankingOnly(run, sensitivity);

        return new ScoreboardRow(
            Vertical: slug,
            State: RowState.Placed,
            Run: run,
            ShareOfAll: (double)run.CorrectQuestions / run.ScoredQuestions,
            ShareOfReachable: shareOfReachable,
            Ceiling: ceiling,
            Ranking: ranking,
            Reason: null);
    }

    /// <summary>
    /// Whether a vertical's score is a measurement of write-time supersession.
    /// </summary>
    /// <remarks>
    /// <b>Deliberately a named list of one, not a heuristic.</b> Bitemporal's shapes ask what a fact
    /// was superseded BY and what held at a past instant — questions the store can only answer if
    /// supersession wrote something. Every arm this project has ever run recorded zero
    /// <c>:SUPERSEDED_BY</c> edges, so every bitemporal number ever produced here is one off-state.
    /// <para>
    /// Other verticals are NOT listed. Semantic, temporal and the rest also run against a store with
    /// zero edges, and for them that is irrelevant — supersession is not what their shapes measure,
    /// and voiding them would turn a real constraint into noise that gets ignored.
    /// </para>
    /// </remarks>
    private static bool DependsOnSupersession(string verticalSlug) =>
        string.Equals(verticalSlug, "bitemporal", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The score restricted to shapes that can separate systems under a dense retriever.
    /// </summary>
    /// <remarks>
    /// Three outcomes, kept apart on purpose. <b>Unknown</b> — the corpus declares no sensitivity, so
    /// the restriction cannot be applied and must not be faked. <b>NotRankable</b> — every shape is
    /// non-ranking, which is a finding about the corpus rather than a zero for us. <b>A score</b> —
    /// over the ranking shapes only, with the count of shapes it rests on, because a restriction down
    /// to one shape is a different claim from a restriction down to four.
    /// </remarks>
    private static RankingOnlyScore RankingOnly(
        RunArtifacts run, IReadOnlyDictionary<string, ShapeSensitivity>? sensitivity)
    {
        if (sensitivity is null || run.ByShape.Count == 0) return RankingOnlyScore.Unknown();

        var correct = 0;
        var scored = 0;
        var ranking = 0;
        var nonRanking = new List<string>();
        var unclassified = new List<string>();

        var sensitiveShapes = new List<string>();
        var sensitiveCorrect = 0;
        var sensitiveScored = 0;

        foreach (var (shape, tally) in run.ByShape)
        {
            if (!sensitivity.TryGetValue(shape, out var flag))
            {
                // A shape the corpus does not classify is NOT assumed rankable. Including it would
                // reintroduce, one shape at a time, the assumption this column exists to remove.
                unclassified.Add(shape);
                continue;
            }

            // THE 2026-09-15 RULING: the dense boolean is a THREE-CLASS read, not a filter. The
            // published flag is measured with ada-002 and flips on 4 of 35 shapes against 3-small --
            // and neither embedder is ours. A shape that only one of them ranks is a fact about the
            // embedder, so it is held apart rather than counted or discarded.
            var verdict = TypedMemEvalDenseSecondOpinion.For(run.Vertical, shape)?.Class
                // No second opinion for this shape: fall back to the published flag alone and say so
                // by treating it as SENSITIVE, never as robust. One retriever is not a condition.
                ?? (flag.Discriminates ? DenseRankingClass.RetrieverSensitive : DenseRankingClass.NonRanking);

            switch (verdict)
            {
                case DenseRankingClass.NonRanking:
                    nonRanking.Add(shape);
                    break;

                case DenseRankingClass.RetrieverSensitive:
                    sensitiveShapes.Add(shape);
                    sensitiveCorrect += tally.Correct;
                    sensitiveScored += tally.N - tally.Unrun;
                    break;

                default:
                    ranking++;
                    correct += tally.Correct;
                    scored += tally.N - tally.Unrun;
                    break;
            }
        }

        sensitiveShapes.Sort(StringComparer.Ordinal);

        nonRanking.Sort(StringComparer.Ordinal);
        unclassified.Sort(StringComparer.Ordinal);

        return scored <= 0
            ? RankingOnlyScore.NotRankable(nonRanking, unclassified) with
            {
                SensitiveShapes = sensitiveShapes,
                SensitiveCorrect = sensitiveCorrect,
                SensitiveScored = sensitiveScored,
            }
            : new RankingOnlyScore(
                Score: (double)correct / scored,
                Correct: correct,
                Scored: scored,
                RankingShapes: ranking,
                NonRankingShapes: nonRanking,
                UnclassifiedShapes: unclassified,
                State: RankingState.Scored)
            {
                SensitiveShapes = sensitiveShapes,
                SensitiveCorrect = sensitiveCorrect,
                SensitiveScored = sensitiveScored,
            };
    }

    /// <summary>Prints the scoreboard, or the reason it refuses to be one.</summary>
    internal static void Print(Scoreboard scoreboard, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(scoreboard);
        ArgumentNullException.ThrowIfNull(writer);

        writer.WriteLine($"typedmemeval scoreboard — arm '{scoreboard.ArmToken}'");

        foreach (var refusal in scoreboard.Refusals)
        {
            writer.WriteLine($"  ⛔ REFUSED: {refusal}");
        }

        if (scoreboard.Refusals.Count > 0)
        {
            writer.WriteLine(
                "  No scoreboard is printed. Rows that disagree on lineage, judge or arm are not "
                + "comparable, and a table would make them look as though they were.");
            return;
        }

        foreach (var note in scoreboard.Notes)
        {
            writer.WriteLine($"  ⚠ {note}");
        }

        // The judge is what every row must share; the releases are in the note above when they
        // differ, and repeating four 40-character build hashes here would bury the table.
        writer.WriteLine($"  judge {Short(scoreboard.JudgePromptFingerprint)}");
        writer.WriteLine(
            "  vertical          share-of-all   share-of-reachable   robust-ranking                     corpus");

        foreach (var row in scoreboard.Rows)
        {
            writer.WriteLine(Format(row));
        }

        var placed = scoreboard.Rows.Count(row => row.State == RowState.Placed);
        writer.WriteLine(string.Create(
            CultureInfo.InvariantCulture,
            $"  {placed} of {scoreboard.Rows.Count} verticals placed."));
    }

    private static string Format(ScoreboardRow row)
    {
        var name = row.Vertical.PadRight(16);

        return row.State switch
        {
            RowState.NotPlaced => $"  {name}  — NOT PLACED: {row.Reason}",
            RowState.Void => $"  {name}  — VOID: {row.Reason}; no score is reported",
            RowState.OffState => $"  {name}  — ⛔ OFF-STATE: {row.Reason}",
            _ => string.Create(
                CultureInfo.InvariantCulture,
                $"  {name}  {Pct(row.ShareOfAll),12}   {Pct(row.ShareOfReachable),18}   "
                + $"{Ranking(row.Ranking),-34}  {Short(row.Run!.Value.CorpusSha256)}"),
        };

        static string Pct(double? value) =>
            value is { } v ? v.ToString("P1", CultureInfo.InvariantCulture) : "NOT KNOWN";

        static string Ranking(RankingOnlyScore ranking)
        {
            // The sensitive tail is APPENDED, never merged. A reader must be able to see that a
            // vertical's robust score rests on two shapes while a third flips with the embedder.
            var tail = ranking.SensitiveShapes.Count == 0
                ? string.Empty
                : string.Create(
                    CultureInfo.InvariantCulture,
                    $" +{ranking.SensitiveShapes.Count} sensitive ({ranking.SensitiveCorrect}/{ranking.SensitiveScored})");

            return ranking.State switch
            {
                RankingState.Unknown => "UNKNOWN",
                RankingState.NotRankable => $"⛔ NO ROBUST SHAPE{tail}",
                _ => string.Create(
                    CultureInfo.InvariantCulture,
                    $"{ranking.Score:P1} ({ranking.Correct}/{ranking.Scored}, {ranking.RankingShapes} robust)") + tail,
            };
        }
    }

    private static string Short(string? hash) =>
        hash is { Length: >= 12 } ? hash[..12] : hash ?? "(none)";

    /// <summary>A release without its 40-character build metadata, which no reader needs inline.</summary>
    private static string Build(string? version)
    {
        if (version is null) return "(unrecorded)";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : $"{version[..plus]}+{Short(version[(plus + 1)..])}";
    }
}

/// <summary>One assembled scoreboard, or the refusals that stop it being one.</summary>
internal sealed class Scoreboard
{
    internal Scoreboard(IReadOnlyList<ScoreboardRow> rows, string armToken)
    {
        Rows = rows;
        ArmToken = armToken;

        var placed = rows.Where(row => row.State == RowState.Placed).Select(row => row.Run!.Value).ToArray();

        var judges = placed.Select(run => run.JudgePromptFingerprint).Distinct(StringComparer.Ordinal).ToArray();
        var builds = placed.Select(run => run.AgentEvalVersion)
            .Where(version => version is not null)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var refusals = new List<string>();

        // Corpus identity is settled row by row before a row is placed at all, so the residual check
        // is the judge. Two cells graded by different judges are not comparable however identical
        // their corpora, and this refuses the whole table rather than footnoting a row: a footnote on
        // a table of numbers is read after the numbers, if at all.
        if (judges.Length > 1)
        {
            refusals.Add(
                $"{judges.Length} distinct judge prompt fingerprints across placed rows — "
                + "these cells were graded by different judges and cannot be ranked against each other");
        }

        Refusals = refusals;
        JudgePromptFingerprint = judges.Length == 1 ? judges[0] : null;
        AgentEvalVersions = builds;

        // Several releases among placed rows is EXPECTED and not a problem: seven of the ten corpora
        // were byte-identical across 0.34-0.36, so a run labelled with an older release can be a
        // measurement of exactly this corpus -- which is what the per-row sha gate established before
        // the row was placed. It is stated rather than hidden, because a reader who sees two version
        // numbers deserves to know why that is allowed here.
        Notes = builds.Length > 1
            ? [$"placed rows come from {builds.Length} releases ({string.Join(", ", builds.Select(Build))}) — "
               + "admitted because each row's corpus sha matches the corpus this build carries, "
               + "which is the field that decides comparability"]
            : [];
    }

    /// <summary>A release without its 40-character build metadata, which no reader needs inline.</summary>
    private static string Build(string? version)
    {
        if (version is null) return "(unrecorded)";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    internal IReadOnlyList<ScoreboardRow> Rows { get; }

    internal string ArmToken { get; }

    /// <summary>Non-empty when the rows cannot legitimately share a table.</summary>
    internal IReadOnlyList<string> Refusals { get; }

    /// <summary>Things a reader should check, which do not by themselves invalidate the table.</summary>
    internal IReadOnlyList<string> Notes { get; }

    internal string? JudgePromptFingerprint { get; }

    /// <summary>The releases the placed rows were produced under — often more than one, legitimately.</summary>
    internal IReadOnlyList<string?> AgentEvalVersions { get; }
}

/// <summary>One vertical's row: a score, or a reason there is none.</summary>
internal readonly record struct ScoreboardRow(
    string Vertical,
    RowState State,
    RunArtifacts? Run,
    double? ShareOfAll,
    double? ShareOfReachable,
    ReachableCeiling? Ceiling,
    RankingOnlyScore Ranking,
    string? Reason)
{
    internal static ScoreboardRow NotPlaced(string vertical, string reason) =>
        new(vertical, RowState.NotPlaced, null, null, null, null, RankingOnlyScore.Unknown(), reason);

    internal static ScoreboardRow Void(string vertical, RunArtifacts run, string reason) =>
        new(vertical, RowState.Void, run, null, null, null, RankingOnlyScore.Unknown(), reason);

    internal static ScoreboardRow OffState(string vertical, RunArtifacts run, string reason) =>
        new(vertical, RowState.OffState, run, null, null, null, RankingOnlyScore.Unknown(), reason);
}

/// <summary>Why a row carries no number — each distinct, because they mean different things.</summary>
internal enum RowState
{
    /// <summary>
    /// No placeable run: none on this arm, or none against the corpus this build carries. Not a zero.
    /// </summary>
    NotPlaced,

    /// <summary>The run exists and its measurements are missing. Not a low score.</summary>
    Void,

    /// <summary>
    /// The run completed and the mechanism it measures never fired. Not a score, and not missing
    /// data either — a measurement of an absence.
    /// </summary>
    OffState,

    /// <summary>Scored.</summary>
    Placed,
}

/// <summary>The fields of a run that decide whether it may sit beside another one.</summary>
internal readonly record struct RunArtifacts(
    string Vertical,
    DateTimeOffset StartedUtc,
    string? CorpusSha256,
    string? JudgePromptFingerprint,
    string? AgentEvalVersion,
    int SelectedQuestions,
    int CorpusQuestions,
    int ScoredQuestions,
    int CorrectQuestions,
    int AgentFailureQuestions,
    int? SupersededByEdges,
    IReadOnlyDictionary<string, ShapeTally> ByShape);

/// <summary>One shape's tally, as the typed outcomes record it.</summary>
internal readonly record struct ShapeTally(int N, int Correct, int Unrun);

/// <summary>The third column: a score over ranking shapes, or a statement that there is none.</summary>
internal readonly record struct RankingOnlyScore(
    double Score,
    int Correct,
    int Scored,
    int RankingShapes,
    IReadOnlyList<string> NonRankingShapes,
    IReadOnlyList<string> UnclassifiedShapes,
    RankingState State)
{
    /// <summary>Shapes whose ranking power flips between the two published dense retrievers.</summary>
    /// <remarks>
    /// Held apart from the score rather than folded into it. Counting them would let a fact about an
    /// embedder move a number about the engine; discarding them would throw away a cell that one of
    /// the two published retrievers does rank. They are reported beside the robust score so a reader
    /// can see how much of the vertical is resting on them — and never load-bearing alone.
    /// </remarks>
    internal IReadOnlyList<string> SensitiveShapes { get; init; } = [];

    internal int SensitiveCorrect { get; init; }

    internal int SensitiveScored { get; init; }

    internal static RankingOnlyScore Unknown() =>
        new(0, 0, 0, 0, [], [], RankingState.Unknown);

    internal static RankingOnlyScore NotRankable(
        IReadOnlyList<string> nonRanking, IReadOnlyList<string> unclassified) =>
        new(0, 0, 0, 0, nonRanking, unclassified, RankingState.NotRankable);
}

/// <summary>Whether the ranking-only restriction could be applied, and what it found.</summary>
internal enum RankingState
{
    /// <summary>The corpus declares no sensitivity — the restriction cannot be applied.</summary>
    Unknown,

    /// <summary>Every shape is non-ranking. A finding about the corpus, not a zero for us.</summary>
    NotRankable,

    /// <summary>Scored over the ranking shapes.</summary>
    Scored,
}
