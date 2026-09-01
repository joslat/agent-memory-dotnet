using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The re-grade replay must not be able to understate a baseline (2026-08-28).
/// </summary>
/// <remarks>
/// <para>
/// The judge-only re-grade exists because AgentEval's bitemporal judge had no body, so every stored
/// bitemporal verdict is suspect while the answers behind them are sound. The re-graded numbers will
/// become the baselines a pre-registered arm is adjudicated against, which puts an unusual burden on
/// this adapter: a silent miss here does not crash, it lowers a baseline.
/// </para>
/// <para>
/// A question with no stored answer is replayed as empty text, and an empty answer grades as WRONG
/// rather than as MISSING. That is indistinguishable, in the final score, from the model having
/// genuinely failed — so the miss must be counted and surfaced rather than absorbed.
/// </para>
/// </remarks>
public class TypedMemEvalReplayAdapterTests
{
    private static TypedMemEvalReplayAdapter Adapter(params (string Question, string Answer)[] rows) =>
        new(rows, "m");

    [Fact]
    public async Task AStoredAnswerIsReplayedVerbatim()
    {
        // Verbatim matters more than it looks: the judge grades this text, so any normalisation here
        // would be a silent edit to the thing under measurement.
        var adapter = Adapter(("Where does Colm work?", "Marchmont, since February."));

        var response = await adapter.InvokeAsync("Where does Colm work?");

        response.Text.Should().Be("Marchmont, since February.");
        adapter.Matched.Should().Be(1);
        adapter.UnmatchedQuestions.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnexpectedQuestionIsRefusedRatherThanScoredAsWrong()
    {
        var adapter = Adapter(("Known question", "answer"));

        var response = await adapter.InvokeAsync("A question the artifact never contained");

        response.Text.Should().BeEmpty();
        adapter.Matched.Should().Be(0);
        // The safety property: the caller sees it and aborts, instead of publishing a baseline
        // quietly reduced by one wrong answer that was never actually wrong.
        adapter.OrderingMismatches.Should().ContainSingle()
            .Which.Should().Be("A question the artifact never contained");
    }

    [Fact]
    public async Task AQuestionOutOfOrderIsRefusedRatherThanPairedWithTheWrongAnswer()
    {
        // The failure this class exists to prevent. Pairing answers with the wrong questions yields
        // a complete, plausible, entirely meaningless score -- far worse than an obvious blank.
        var adapter = Adapter(("First question", "first answer"), ("Second question", "second answer"));

        await adapter.InvokeAsync("Second question");

        adapter.Matched.Should().Be(0);
        adapter.OrderingMismatches.Should().ContainSingle();
    }

    [Fact]
    public async Task DuplicateQuestionTextsAreReplayedDistinctlyByPosition()
    {
        // Twelve of the sixty bitemporal questions share text with a question whose gold answer
        // DIFFERS -- tme-bit-007 and tme-bit-037 both ask about Colm Whitaker in February and answer
        // Lowick and Marchmont. Text keying silently mispaired 12 of 60; position keying does not.
        var adapter = Adapter(
            ("Which department was Colm Whitaker at in February?", "Lowick"),
            ("Which department was Colm Whitaker at in February?", "Marchmont"));

        var first = await adapter.InvokeAsync("Which department was Colm Whitaker at in February?");
        var second = await adapter.InvokeAsync("Which department was Colm Whitaker at in February?");

        first.Text.Should().Be("Lowick");
        second.Text.Should().Be("Marchmont");
        adapter.Matched.Should().Be(2);
        adapter.OrderingMismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task MoreQuestionsThanStoredAnswersIsRecordedAsUnmatched()
    {
        var adapter = Adapter(("only question", "only answer"));

        await adapter.InvokeAsync("only question");
        await adapter.InvokeAsync("a question past the end of the artifact");

        adapter.Matched.Should().Be(1);
        adapter.UnmatchedQuestions.Should().ContainSingle();
    }

    [Fact]
    public async Task AReplayIsUnaffectedByAStoredRowWhoseVerdictWasNull()
    {
        // The ON re-grade crashed on a question the new judge returned NO verdict for, and the crash
        // sat in the AGREEMENT diagnostic -- which ran before the artifact was persisted, so a full
        // judge pass was destroyed by a statistic. The replay itself must be indifferent to verdicts;
        // it only ever hands back answer text.
        var adapter = Adapter(("q", "a"));

        var response = await adapter.InvokeAsync("q");

        response.Text.Should().Be("a");
        adapter.Matched.Should().Be(1);
        adapter.OrderingMismatches.Should().BeEmpty();
    }

    [Fact]
    public async Task TheAccountingListsCannotBeMutatedThroughTheirPublicProperties()
    {
        // Copilot review, PR #209: these returned the backing lists, so a caller could downcast and
        // mutate the accounting the re-grade gate depends on -- and that gate's whole job is to abort
        // a run whose baseline would otherwise be silently understated.
        var adapter = Adapter(("q", "a"));
        await adapter.InvokeAsync("unexpected question");

        var mismatches = adapter.OrderingMismatches;
        (mismatches as List<string>)?.Clear();

        adapter.OrderingMismatches.Should().ContainSingle(
            "the property must hand out a snapshot, not the list the adapter counts with");
    }

    [Fact]
    public async Task PositionKeyingCannotDetectAGoldThatMovedBehindUnchangedText()
    {
        // Documents the LIMIT of this adapter, and why the re-grade needs a corpus-sha gate above it.
        // AgentEval redraws corpora keeping the question_id set identical with zero byte-identical
        // items; for bitemporal, 27 of 60 keep the SAME question text with a DIFFERENT gold. The
        // replay verifies text per position and is therefore blind to exactly that case -- it will
        // happily replay, and the answers will be graded against keys they were never produced for.
        //
        // This test asserts the blindness rather than pretending it away: the guard that catches it
        // lives in TypedMemEvalRegradeProgram, which compares corpus_sha256 before spending.
        var adapter = Adapter(("Which department was Colm Whitaker at in February?", "Lowick"));

        var response = await adapter.InvokeAsync("Which department was Colm Whitaker at in February?");

        response.Text.Should().Be("Lowick");
        adapter.OrderingMismatches.Should().BeEmpty(
            "identical text passes the positional check no matter what the gold now says — which is "
            + "why corpus identity must be established before the replay runs at all");
    }

    [Fact]
    public async Task TheHistoryHooksAreInertAndDoNotDisturbReplay()
    {
        // Implemented only so the runner takes the same branches it takes for the real adapter. If
        // they ever stop being inert, a re-grade stops being a pure judge pass.
        var adapter = Adapter(("q", "a"));

        adapter.InjectConversationHistory([("user", "assistant")]);
        await adapter.ResetSessionAsync();
        var response = await adapter.InvokeAsync("q");

        response.Text.Should().Be("a");
        adapter.Matched.Should().Be(1);
    }
}
