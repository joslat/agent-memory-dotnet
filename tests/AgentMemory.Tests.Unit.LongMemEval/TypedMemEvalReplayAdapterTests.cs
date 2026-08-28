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
        new(rows.ToDictionary(row => row.Question, row => row.Answer, StringComparer.Ordinal), "m");

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
    public async Task AQuestionWithNoStoredAnswerIsRecordedAsUnmatchedRatherThanScoredAsWrong()
    {
        var adapter = Adapter(("Known question", "answer"));

        var response = await adapter.InvokeAsync("A question the artifact never contained");

        response.Text.Should().BeEmpty();
        adapter.Matched.Should().Be(0);
        // The whole safety property: the caller can see the miss and abort, instead of publishing a
        // baseline quietly reduced by one wrong answer that was never actually wrong.
        adapter.UnmatchedQuestions.Should().ContainSingle()
            .Which.Should().Be("A question the artifact never contained");
    }

    [Fact]
    public async Task MatchingIsExactSoANearMissIsAMissRatherThanAWrongPairing()
    {
        // Trailing whitespace, casing, punctuation: pairing a question with a DIFFERENT question's
        // answer would be worse than not pairing it at all, because it would be graded and scored.
        var adapter = Adapter(("Where does Colm work?", "Marchmont"));

        await adapter.InvokeAsync("where does colm work?");

        adapter.Matched.Should().Be(0);
        adapter.UnmatchedQuestions.Should().ContainSingle();
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
