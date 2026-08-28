using AgentMemory.Abstractions.Domain;
using AgentMemory.LongMemEval;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.LongMemEval;

/// <summary>
/// The 30.9d render-state gate must fail closed (validity requirement, 2026-08-28).
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect this guards.</b> Twice inside one intervention a lever was believed live because it
/// was reachable — <c>SupersedeReplacedFacts</c>, then <c>ResolveSupersessions</c> — and in both
/// cases a config echo would have confirmed the wrong thing, because the flag that was set was not
/// the flag that mattered. The ruling on this arm therefore requires POSITIVE evidence drawn from the
/// run's own output before any score may be read.
/// </para>
/// <para>
/// So these tests apply the counter-check to the check itself: <i>would this gate still pass if the
/// thing it guards were deleted?</i> A gate that answers "yes" is decoration.
/// </para>
/// </remarks>
public class SupersessionRenderProbeTests
{
    private const string Note = "(since 2023-05-12; previously Globex)";

    private static MemoryContext ContextWith(params string?[] notes) => new()
    {
        SessionId = "s1",
        AssembledAtUtc = DateTimeOffset.UnixEpoch,
        Projection = new ProjectedContext
        {
            Annotations = notes
                .Select((note, index) => (Id: $"fact-{index}", Note: note))
                .ToDictionary(
                    pair => pair.Id,
                    pair => new ProjectedItemAnnotation { SupersessionNote = pair.Note },
                    StringComparer.Ordinal)
        }
    };

    [Fact]
    public void ARunThatRenderedNothingIsNotConfirmed()
    {
        // The dark-arm case: this is precisely the state every scored run was in before 30.9d, and
        // the state whose scores must NOT be read.
        var probe = new LongMemEvalSupersessionRenderProbe();
        probe.Observe("q1", ContextWith(), "Retrieved memory:\n[fact] acme employs colm\n");

        var summary = LongMemEvalSupersessionRenderSummary.From(probe.Samples);

        summary.PromptsInspected.Should().Be(1);
        summary.NotesAnnotated.Should().Be(0);
        summary.RenderConfirmed.Should().BeFalse();
    }

    [Fact]
    public void ANoteThatNeverReachedThePromptIsNotConfirmed()
    {
        // The render-surface bug the two counts exist to separate. Projection did its job; the
        // harness's own prompt builder dropped the note. One count would have called this success.
        var probe = new LongMemEvalSupersessionRenderProbe();
        probe.Observe("q1", ContextWith(Note), "Retrieved memory:\n[fact] acme employs colm\n");

        var summary = LongMemEvalSupersessionRenderSummary.From(probe.Samples);

        summary.NotesAnnotated.Should().Be(1);
        summary.NotesInPrompt.Should().Be(0);
        summary.RenderConfirmed.Should().BeFalse();
    }

    [Fact]
    public void ANoteCarriedIntoThePromptIsConfirmedAndKeepsASample()
    {
        var probe = new LongMemEvalSupersessionRenderProbe();
        probe.Observe("q1", ContextWith(Note), $"Retrieved memory:\n[fact] acme employs colm {Note}\n");

        var summary = LongMemEvalSupersessionRenderSummary.From(probe.Samples);

        summary.NotesInPrompt.Should().Be(1);
        summary.RenderConfirmed.Should().BeTrue();
        // A retained sample, not merely a positive counter: the gate must be eyeballable, because a
        // number is exactly what the previous two dark runs also produced.
        summary.Sample.Should().Be(Note);
    }

    [Fact]
    public void QuestionsWithoutSupersessionStillCountTowardTheDenominator()
    {
        // Most questions in any corpus have nothing superseded. Dropping those rows would leave
        // PromptsInspected silently meaning "prompts that had notes", which reads as full coverage.
        var probe = new LongMemEvalSupersessionRenderProbe();
        probe.Observe("q1", ContextWith(), "Retrieved memory:\n");
        probe.Observe("q2", ContextWith(Note), $"Retrieved memory:\n[fact] x {Note}\n");

        var summary = LongMemEvalSupersessionRenderSummary.From(probe.Samples);

        summary.PromptsInspected.Should().Be(2);
        summary.PromptsWithNoteInPrompt.Should().Be(1);
        summary.RenderConfirmed.Should().BeTrue();
    }

    [Fact]
    public void AnAbsentProjectionIsRecordedAsNotMeasuredRatherThanAsZeroRendered()
    {
        // A context with no Projection at all is the off-state, and it must be indistinguishable from
        // "the feature is off" rather than being reported as a rendering failure.
        var probe = new LongMemEvalSupersessionRenderProbe();
        probe.Observe(
            "q1",
            new MemoryContext { SessionId = "s1", AssembledAtUtc = DateTimeOffset.UnixEpoch },
            "Retrieved memory:\n");

        var summary = LongMemEvalSupersessionRenderSummary.From(probe.Samples);

        summary.NotesAnnotated.Should().Be(0);
        summary.Sample.Should().BeNull();
        summary.RenderConfirmed.Should().BeFalse();
    }
}
