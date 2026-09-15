using System.Text.Json;
using AgentEval.Memory.External.TypedMemEval;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The ten-vertical scoreboard: three columns, and a refusal wherever a cell cannot carry a number.
/// </summary>
/// <remarks>
/// <para>
/// Every test here pins a way the table could lie. A vertical that never ran must not read as a zero;
/// a run whose questions died must not read as a low score; rows graded by two judges must not sit in
/// one table at all; and a shape that cannot separate systems under a dense retriever must not
/// contribute to the column that claims it can.
/// </para>
/// <para>
/// The sensitivity and ceiling data are the real ones shipped in the pinned package — these tests
/// build artifacts, never corpora, so a redrawn corpus moves them rather than leaving them asserting
/// numbers that no longer exist.
/// </para>
/// </remarks>
public sealed class ScoreboardTests : IDisposable
{
    private readonly string _directory =
        Directory.CreateTempSubdirectory("scoreboard-tests-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    /// <summary>A vertical with no run is ABSENT — the state, not a zero.</summary>
    /// <remarks>
    /// A table of numbers makes "never ran" and "scored nothing" identical, and they are the opposite
    /// of each other: one is missing evidence, the other is evidence.
    /// </remarks>
    [Fact]
    public void AVerticalWithNoRunIsAbsentRatherThanZero()
    {
        Write("semantic", correct: 9, scored: 10);

        var scoreboard = TypedMemEvalScoreboard.Assemble(_directory, "default");

        var conjunction = Row(scoreboard, "conjunction");
        conjunction.State.Should().Be(RowState.NotPlaced);
        conjunction.ShareOfAll.Should().BeNull("an absent vertical has no score, not a score of zero");
    }

    /// <summary>All ten verticals appear, whether or not they ran.</summary>
    [Fact]
    public void EveryShippedVerticalGetsARow()
    {
        var scoreboard = TypedMemEvalScoreboard.Assemble(_directory, "default");

        scoreboard.Rows.Select(row => row.Vertical)
            .Should().BeEquivalentTo(TypedMemEvalVerticals.All.Select(vertical => vertical.Slug));
    }

    /// <summary>A run carrying agent failures is VOID, not low-scoring.</summary>
    /// <remarks>
    /// Two paid runs in this project died mid-flight and reported 7/39 as though it were a result. An
    /// agent failure is an absent measurement; pooling it with wrong answers is how a dead run scores.
    /// </remarks>
    [Fact]
    public void AgentFailuresVoidTheRowInsteadOfLoweringIt()
    {
        Write("semantic", correct: 7, scored: 39, agentFailures: 11);

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "semantic");

        row.State.Should().Be(RowState.Void);
        row.ShareOfAll.Should().BeNull("a void row reports no score at all");
    }

    /// <summary>
    /// Bitemporal scored against a store where supersession never fired is an OFF-STATE, not a score.
    /// </summary>
    /// <remarks>
    /// Every arm this project has run recorded zero <c>:SUPERSEDED_BY</c> edges, so every bitemporal
    /// number ever produced here measures the absence of the mechanism its shapes ask about. Placing
    /// it beside the other nine as a percentage is the one claim this project holds as absolutely
    /// forbidden — and a scoreboard that assembles itself is exactly where it would slip back in.
    /// </remarks>
    [Fact]
    public void BitemporalWithNoSupersessionEdgesIsAnOffStateRatherThanAScore()
    {
        Write("bitemporal", correct: 33, scored: 60, supersededByEdges: 0);

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "bitemporal");

        row.State.Should().Be(RowState.OffState);
        row.ShareOfAll.Should().BeNull("an off-state carries no score at all");
        row.Reason.Should().Contain("never fired");
    }

    /// <summary>The same run WITH supersession firing does score — the gate is the edges, not the name.</summary>
    [Fact]
    public void BitemporalScoresOnceSupersessionActuallyFires()
    {
        Write("bitemporal", correct: 33, scored: 60, supersededByEdges: 412);

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "bitemporal");

        row.State.Should().Be(RowState.Placed);
        row.ShareOfAll.Should().BeApproximately(33d / 60, 1e-9);
    }

    /// <summary>
    /// A zero edge count does NOT void the verticals that do not measure supersession.
    /// </summary>
    /// <remarks>
    /// Every vertical runs against a store with zero edges. If that voided all of them the constraint
    /// would be noise, and noise gets switched off — which is how a real gate stops working.
    /// </remarks>
    [Fact]
    public void ZeroSupersessionEdgesDoNotVoidVerticalsThatDoNotMeasureIt()
    {
        Write("semantic", correct: 45, scored: 50, supersededByEdges: 0);

        Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "semantic")
            .State.Should().Be(RowState.Placed);
    }

    /// <summary>An unrecorded edge count is unknown, and unknown is not "it fired".</summary>
    [Fact]
    public void BitemporalWithNoRecordedEdgeCountIsNotSilentlyScored()
    {
        Write("bitemporal", correct: 33, scored: 60, supersededByEdges: null);

        Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "bitemporal")
            .State.Should().NotBe(RowState.Placed);
    }

    /// <summary>Two judges means no table, not a footnote.</summary>
    [Fact]
    public void RowsGradedByDifferentJudgesRefuseToShareATable()
    {
        Write("semantic", correct: 9, scored: 10, judge: new string('a', 64));
        Write("conjunction", correct: 5, scored: 10, judge: new string('b', 64));

        var scoreboard = TypedMemEvalScoreboard.Assemble(_directory, "default");

        scoreboard.Refusals.Should().ContainSingle()
            .Which.Should().Contain("judge prompt fingerprints");
    }

    /// <summary>
    /// A run against a different draw of the corpus does not place — and does not refuse the table.
    /// </summary>
    /// <remarks>
    /// AgentEval redraw corpora keeping question ids identical with zero byte-identical items, so the
    /// sha is the only field that separates two draws. If a stale draw refused the table, no
    /// scoreboard could be assembled from a real archive; if it filled a row, a score measured against
    /// different gold would sit beside current ones. It does neither.
    /// </remarks>
    [Fact]
    public void ARunAgainstADifferentCorpusDrawDoesNotPlace()
    {
        Write("semantic", correct: 9, scored: 10, corpusSha: new string('e', 64));
        Write("conjunction", correct: 5, scored: 10);

        var scoreboard = TypedMemEvalScoreboard.Assemble(_directory, "default");

        scoreboard.Refusals.Should().BeEmpty();
        Row(scoreboard, "semantic").State.Should().Be(RowState.NotPlaced);
        Row(scoreboard, "conjunction").State.Should().Be(RowState.Placed);
    }

    /// <summary>A one-question stage-2 probe never becomes a row, however new it is.</summary>
    /// <remarks>
    /// Every full run here is preceded by a single-question run that proves the chain end to end. It
    /// lands in the same directory, on the same arm, against the same corpus — and stays the newest
    /// artifact for the hours the real run takes. Without this, the scoreboard reported prospective as
    /// 0.0% from a sample of one, minutes after that probe launched.
    /// </remarks>
    [Fact]
    public void AStageTwoProbeDoesNotBecomeARow()
    {
        Write("prospective", correct: 0, scored: 1, selected: 1, corpusQuestions: 72,
            started: "2026-09-14T17:41:00Z", stamp: "stage2");

        Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "prospective")
            .State.Should().Be(RowState.NotPlaced);
    }

    /// <summary>And it does not displace the full run it precedes, either.</summary>
    [Fact]
    public void AStageTwoProbeDoesNotDisplaceTheFullRunItPrecedes()
    {
        Write("prospective", correct: 40, scored: 72, selected: 72, corpusQuestions: 72,
            started: "2026-09-14T10:00:00Z", stamp: "full");
        Write("prospective", correct: 0, scored: 1, selected: 1, corpusQuestions: 72,
            started: "2026-09-14T17:41:00Z", stamp: "stage2");

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "prospective");

        row.State.Should().Be(RowState.Placed);
        row.ShareOfAll.Should().BeApproximately(40d / 72, 1e-9);
    }

    /// <summary>An artifact recording no corpus sha is UNVERIFIABLE, which is not a match.</summary>
    /// <remarks>
    /// Absence of evidence is not evidence of identity. Admitting it would be the whole gate reduced
    /// to a formality, since the artifacts that predate provenance capture are exactly the ones most
    /// likely to come from an earlier draw.
    /// </remarks>
    [Fact]
    public void AnArtifactWithNoCorpusShaDoesNotPlace()
    {
        Write("semantic", correct: 9, scored: 10, corpusSha: null);

        Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "semantic")
            .State.Should().Be(RowState.NotPlaced);
    }

    /// <summary>
    /// Rows produced under different releases DO share a table when their corpus shas match.
    /// </summary>
    /// <remarks>
    /// Seven of the ten corpora were byte-identical across 0.34-0.36. A version gate would have read
    /// procedural, temporal and workingmemory as absent and sent three finished verticals back for an
    /// ~11h re-run that could not have changed a single answer. The standing rule is to compare
    /// corpora on sha, and this is that rule.
    /// </remarks>
    [Fact]
    public void RowsFromDifferentReleasesShareATableWhenTheirCorporaMatch()
    {
        Write("semantic", correct: 9, scored: 10, version: "0.35.0-beta+aaaaaaaaaaaa");
        Write("conjunction", correct: 5, scored: 10, version: "0.36.0-beta+bbbbbbbbbbbb");

        var scoreboard = TypedMemEvalScoreboard.Assemble(_directory, "default");

        scoreboard.Refusals.Should().BeEmpty();
        scoreboard.Rows.Count(row => row.State == RowState.Placed).Should().Be(2);
        scoreboard.Notes.Should().ContainSingle().Which.Should().Contain("2 releases");
    }

    /// <summary>One judge and one release assemble cleanly.</summary>
    [Fact]
    public void OneJudgeAndOneReleaseAssemble()
    {
        Write("semantic", correct: 9, scored: 10);
        Write("conjunction", correct: 5, scored: 10);

        var scoreboard = TypedMemEvalScoreboard.Assemble(_directory, "default");

        scoreboard.Refusals.Should().BeEmpty();
        scoreboard.Rows.Count(row => row.State == RowState.Placed).Should().Be(2);
    }

    /// <summary>A run on another arm does not place on this arm's scoreboard.</summary>
    /// <remarks>
    /// Rows 54-55 are sealed as an OFF arm precisely because an ON-arm number placed beside OFF-arm
    /// numbers reads as an improvement in the engine rather than a change of flags.
    /// </remarks>
    [Fact]
    public void ARunOnAnotherArmDoesNotPlace()
    {
        Write("semantic", correct: 9, scored: 10, arm: "expand-qrel");

        Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "semantic")
            .State.Should().Be(RowState.NotPlaced);
    }

    /// <summary>The newest run for a vertical wins; older ones do not accumulate.</summary>
    [Fact]
    public void TheNewestRunPerVerticalIsTheOnePlaced()
    {
        Write("semantic", correct: 1, scored: 10, started: "2026-09-01T00:00:00Z", stamp: "old");
        Write("semantic", correct: 9, scored: 10, started: "2026-09-14T00:00:00Z", stamp: "new");

        Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "semantic")
            .ShareOfAll.Should().BeApproximately(0.9, 1e-9);
    }

    /// <summary>
    /// The ranking-only column excludes shapes a dense retriever cannot separate systems on.
    /// </summary>
    /// <remarks>
    /// Semantic's <c>source-attribution</c> is declared non-ranking under a dense retriever. Scoring
    /// it perfectly and including it would inflate the one column that is supposed to say what the
    /// measurement can actually distinguish.
    /// </remarks>
    [Fact]
    public void TheRankingColumnExcludesNonRankingShapes()
    {
        Write("semantic", correct: 20, scored: 30, shapes: new()
        {
            ["co-reference"] = (N: 15, Correct: 5),          // ranks
            ["source-attribution"] = (N: 15, Correct: 15),   // does NOT rank under dense
        });

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "semantic");

        row.Ranking.State.Should().Be(RankingState.Scored);
        row.Ranking.Scored.Should().Be(15, "only the ranking shape counts");
        row.Ranking.Correct.Should().Be(5);
        row.Ranking.NonRankingShapes.Should().ContainSingle().Which.Should().Be("source-attribution");
        row.ShareOfAll.Should().BeApproximately(20d / 30, 1e-9,
            "share-of-all is unrestricted — the restriction is a separate column, not a correction");
    }

    /// <summary>A vertical whose measured shapes all fail to rank is NOT RANKABLE, not 0%.</summary>
    /// <remarks>
    /// This is a finding about the corpus under our retriever, and printing it as a zero would read as
    /// a catastrophic engine result instead.
    /// </remarks>
    [Fact]
    public void AVerticalWithNoRankingShapeSaysSoInsteadOfScoringZero()
    {
        Write("semantic", correct: 15, scored: 15, shapes: new()
        {
            ["source-attribution"] = (N: 15, Correct: 15),
        });

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "semantic");

        row.Ranking.State.Should().Be(RankingState.NotRankable);
        row.Ranking.Score.Should().Be(0);
        row.ShareOfAll.Should().Be(1.0, "the vertical did score — it just scored on an unrankable shape");
    }

    /// <summary>A shape the corpus does not classify is excluded, never assumed rankable.</summary>
    [Fact]
    public void AnUnclassifiedShapeIsNotAssumedRankable()
    {
        Write("semantic", correct: 20, scored: 30, shapes: new()
        {
            ["co-reference"] = (N: 15, Correct: 5),
            ["a-shape-this-corpus-never-declared"] = (N: 15, Correct: 15),
        });

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "semantic");

        row.Ranking.Scored.Should().Be(15);
        row.Ranking.UnclassifiedShapes.Should()
            .ContainSingle().Which.Should().Be("a-shape-this-corpus-never-declared");
    }

    /// <summary>
    /// Share-of-reachable uses the corpus ceiling, so a capped corpus scores above its raw share.
    /// </summary>
    /// <remarks>
    /// Conjunction caps at 0.947 with 13 of 65 questions structurally unanswerable at k_ref=5, so the
    /// same correct count is a larger share of what was actually reachable.
    /// </remarks>
    [Fact]
    public void ShareOfReachableExceedsShareOfAllOnACappedCorpus()
    {
        Write("conjunction", correct: 20, scored: 65);

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "conjunction");

        row.Ceiling.Should().NotBeNull();
        row.Ceiling!.Value.CappedQuestions.Should().BeGreaterThan(0);
        row.ShareOfReachable.Should().BeGreaterThan(row.ShareOfAll!.Value,
            "the denominator is what the corpus allows, not 1.0");
    }

    /// <summary>
    /// The two derived columns are NOT composed — each keeps its own denominator.
    /// </summary>
    /// <remarks>
    /// The ceiling is declared over all of a corpus's questions. Applying it to a numerator already
    /// restricted to ranking shapes would divide a subset by a whole and produce a ratio that means
    /// nothing. They answer different questions and are printed side by side.
    /// </remarks>
    [Fact]
    public void TheReachableColumnIsWholeCorpusAndIgnoresTheRankingRestriction()
    {
        Write("conjunction", correct: 20, scored: 65, shapes: new()
        {
            ["alias-then-count"] = (N: 15, Correct: 0),
            ["value-then-count"] = (N: 50, Correct: 20),
        });

        var row = Row(TypedMemEvalScoreboard.Assemble(_directory, "default"), "conjunction");

        row.ShareOfReachable.Should().BeApproximately(
            row.Ceiling!.Value.ShareOfReachable(20), 1e-9,
            "share-of-reachable is computed from the whole-corpus correct count, never from the "
            + "ranking-restricted one");
    }

    private static ScoreboardRow Row(Scoreboard scoreboard, string vertical) =>
        scoreboard.Rows.Single(row => row.Vertical == vertical);

    /// <summary>Writes a report and its provenance sidecar, the way a real run leaves them.</summary>
    private void Write(
        string vertical,
        int correct,
        int scored,
        int agentFailures = 0,
        string arm = "default",
        string? judge = null,
        string version = "0.36.0-beta",
        string started = "2026-09-14T00:00:00Z",
        string stamp = "run",
        string? corpusSha = "(this build's)",
        int? supersededByEdges = 0,
        int? selected = null,
        int? corpusQuestions = null,
        Dictionary<string, (int N, int Correct)>? shapes = null)
    {
        judge ??= new string('c', 64);

        // Default to the sha this build actually carries, so a fixture is a claim about THIS corpus.
        // Tests that want a stale or unrecorded draw say so explicitly.
        if (corpusSha == "(this build's)") corpusSha = TypedMemEvalCorpusSha.For(vertical);
        shapes ??= new Dictionary<string, (int, int)> { ["only-shape"] = (scored, correct) };

        var reportName = $"typedmemeval-{vertical}-{arm}-{stamp}.json";

        var report = new Dictionary<string, object?>
        {
            ["SelectedQuestions"] = selected ?? scored,
            ["ScoredQuestions"] = scored,
            ["CorrectQuestions"] = correct,
            ["AgentFailureQuestions"] = agentFailures,
            ["TypedOutcomes"] = new Dictionary<string, object?>
            {
                ["Vertical"] = vertical,
                ["CorpusSha256"] = corpusSha,
                ["JudgePromptFingerprint"] = judge,
                ["ByShape"] = shapes.ToDictionary(
                    pair => pair.Key,
                    pair => (object)new Dictionary<string, object?>
                    {
                        ["N"] = pair.Value.N,
                        ["Correct"] = pair.Value.Correct,
                        ["Unrun"] = 0,
                    }),
            },
            ["Provenance"] = new Dictionary<string, object?>
            {
                ["DatasetQuestionCount"] = corpusQuestions ?? scored,
                ["AgentEvalVersion"] = version,
                ["JudgePromptFingerprint"] = judge,
            },
        };

        var sidecar = new Dictionary<string, object?>
        {
            ["schema"] = "typedmemeval-provenance/1",
            ["report"] = reportName,
            ["vertical"] = vertical,
            ["startedUtc"] = started,
            ["arm"] = new Dictionary<string, object?> { ["token"] = arm },
            ["supersessionStore"] = new Dictionary<string, object?>
            {
                ["supersededByEdges"] = supersededByEdges,
            },
        };

        File.WriteAllText(Path.Combine(_directory, reportName), JsonSerializer.Serialize(report));
        File.WriteAllText(
            Path.Combine(_directory, $"typedmemeval-{vertical}-{arm}-{stamp}.provenance.json"),
            JsonSerializer.Serialize(sidecar));
    }
}
