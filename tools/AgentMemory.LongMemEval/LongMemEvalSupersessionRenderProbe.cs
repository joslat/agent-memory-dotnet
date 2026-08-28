using System.Collections.Concurrent;
using AgentMemory.Abstractions.Domain;

namespace AgentMemory.LongMemEval;

/// <summary>
/// Positive evidence that supersession chains actually reached the answer prompt during a run.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a config echo is not acceptable here.</b> Twice inside one intervention a lever was
/// believed live because it was reachable: <c>SupersedeReplacedFacts</c> (the Bitemporal vertical ran
/// against an append-only store) and then <c>ResolveSupersessions</c> (the renderer was dark in every
/// run ever scored). Re-printing the flag we set would have detected neither, because in both cases
/// the flag we set was not the flag that mattered. So this probe reads the OUTPUT: what the
/// projection annotated, and whether that text is present in the prompt string the model was handed.
/// </para>
/// <para>
/// <b>Two counts, deliberately, because they fail differently.</b> <c>NotesAnnotated</c> comes from
/// <see cref="ProjectedContext.Annotations"/> and is authoritative about the feature having run.
/// <c>NotesInPrompt</c> checks the annotation text against the assembled prompt and is authoritative
/// about it having reached the model. Annotated &gt; 0 with InPrompt = 0 is a render-surface defect
/// that a single count would have reported as success -- the harness builds its own prompt text, so
/// "projection produced a note" and "the note was rendered" are genuinely separable claims.
/// </para>
/// <para>
/// <b>The check must not be passable by absence.</b> A zero here means the arm was dark and its
/// scores must not be read; a NULL summary means the probe never ran, which is likewise a failure and
/// never a pass. <see cref="LongMemEvalSupersessionRenderSummary.RenderConfirmed"/> is the only
/// affirmative form, and it requires a retained sample string rather than a nonzero counter, so the
/// claim can be eyeballed rather than trusted.
/// </para>
/// </remarks>
internal sealed class LongMemEvalSupersessionRenderProbe
{
    /// <summary>Bounded so a sidecar cannot inherit a whole prompt; long enough to read the chain.</summary>
    private const int MaxSampleLength = 240;

    private readonly ConcurrentBag<SupersessionRenderSample> _samples = [];

    public IReadOnlyList<SupersessionRenderSample> Samples => _samples.ToArray();

    /// <summary>Records what one question's projection annotated, and how much of it reached the prompt.</summary>
    public void Observe(string questionId, MemoryContext? context, string prompt)
    {
        var notes = context?.Projection?.Annotations.Values
            .Select(annotation => annotation.SupersessionNote)
            .Where(note => !string.IsNullOrWhiteSpace(note))
            .ToArray() ?? [];

        // Recorded even when empty. A question that produced no note is data -- most questions in any
        // corpus have nothing superseded -- and dropping those rows would leave PromptsInspected
        // measuring "prompts with notes", which is the denominator the pass rule needs.
        var inPrompt = notes.Count(note => prompt.Contains(note!, StringComparison.Ordinal));

        _samples.Add(new SupersessionRenderSample(
            questionId,
            notes.Length,
            inPrompt,
            notes.Length == 0 ? null : Truncate(notes[0]!)));
    }

    private static string Truncate(string note) =>
        note.Length <= MaxSampleLength ? note : note[..MaxSampleLength] + "…";
}

/// <summary>One question's supersession-render observation.</summary>
internal sealed record SupersessionRenderSample(
    string QuestionId,
    int NotesAnnotated,
    int NotesInPrompt,
    string? FirstNote);

/// <summary>Run-level supersession-render evidence, written to the provenance sidecar.</summary>
internal sealed record LongMemEvalSupersessionRenderSummary(
    int PromptsInspected,
    int PromptsWithAnnotation,
    int PromptsWithNoteInPrompt,
    int NotesAnnotated,
    int NotesInPrompt,
    string? Sample)
{
    /// <summary>
    /// The pre-registered render-state gate: the arm rendered supersession chains into real prompts.
    /// </summary>
    /// <remarks>
    /// Requires a retained <see cref="Sample"/>, not merely a positive count, so the gate cannot be
    /// satisfied by an arithmetic artifact. A caller that cannot obtain this summary at all must treat
    /// the absence as a failure -- <c>false</c> and "not measured" are the same verdict for a gate,
    /// and only this property may be read as a pass.
    /// </remarks>
    public bool RenderConfirmed => NotesInPrompt > 0 && !string.IsNullOrWhiteSpace(Sample);

    public static LongMemEvalSupersessionRenderSummary From(IReadOnlyList<SupersessionRenderSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        return new LongMemEvalSupersessionRenderSummary(
            samples.Count,
            samples.Count(sample => sample.NotesAnnotated > 0),
            samples.Count(sample => sample.NotesInPrompt > 0),
            samples.Sum(sample => sample.NotesAnnotated),
            samples.Sum(sample => sample.NotesInPrompt),
            samples.FirstOrDefault(sample => sample.NotesInPrompt > 0)?.FirstNote);
    }
}
