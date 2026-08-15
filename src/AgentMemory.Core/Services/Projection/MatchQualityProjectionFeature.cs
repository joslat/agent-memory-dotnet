using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// Renders how well each item actually matched — and says so, once per section, when nothing did.
/// </summary>
/// <remarks>
/// <para>
/// <b>The measured loss this closes.</b> Every long-term vector search is ranked by the index, and
/// every renderer throws the score away, so a 0.72 near-miss reaches the model looking exactly like a
/// 0.99 match. The failure analysis names this the one memory-layer-fixable abstention failure: a
/// question whose best evidence sat at 0.857 coverage produced a confidently confabulated role the
/// user never held, because nothing in the prompt distinguished "this is close" from "this is it".
/// </para>
/// <para>
/// <b>An unscoreable section produces nothing at all.</b> Not zero scores, not near-miss marks, not a
/// no-direct-match line. When a custom service does not implement the scored contract the section's
/// score list is empty, and emitting abstention cues from that would be inventing evidence — the
/// feature would be reporting on its own wiring rather than on the retrieval. This is the feature's
/// own void witness, and it is asserted by test.
/// </para>
/// <para>
/// Pure: no I/O, no repository, no extra round trip. It reads the scored tuples the assembler already
/// computed.
/// </para>
/// </remarks>
internal sealed class MatchQualityProjectionFeature : IProjectionFeature
{
    public bool IsEnabled(MemoryProjectionOptions options) => options.AnnotateMatchQuality;

    public Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        var options = state.Options;

        Apply(state, ProjectionSectionKeys.Entities, state.Entities.Count,
            state.EntityScores.Select(s => (s.Entity.EntityId, s.Score)), options.NearMissThreshold);
        Apply(state, ProjectionSectionKeys.Facts, state.Facts.Count,
            state.FactScores.Select(s => (s.Fact.FactId, s.Score)), options.NearMissThreshold);
        Apply(state, ProjectionSectionKeys.Preferences, state.Preferences.Count,
            state.PreferenceScores.Select(s => (s.Preference.PreferenceId, s.Score)), options.NearMissThreshold);
        // Traces use their own, MEASURED threshold. The shared 0.85 prior sits inside a dead zone where
        // procedure retrieval behaves identically for every value from 0.00 to 0.86 and never abstains.
        Apply(state, ProjectionSectionKeys.Traces, state.Traces.Count,
            state.TraceScores.Select(s => (s.Trace.TraceId, s.Score)), options.TraceNearMissThreshold);

        return Task.CompletedTask;
    }

    private static void Apply(
        ProjectionState state,
        string sectionKey,
        int itemCount,
        IEnumerable<(string Id, double Score)> scores,
        double nearMissThreshold)
    {
        var scored = scores.ToList();

        // Unscoreable, or a section that retrieved nothing: contribute NOTHING. An empty score list
        // against a non-empty section means the provider could not rank it, and a no-direct-match line
        // derived from that would be a fabricated abstention cue.
        if (scored.Count == 0) return;

        foreach (var (id, score) in scored)
        {
            state.Annotate(id, annotation => annotation with
            {
                Score = score,
                IsNearMiss = score < nearMissThreshold,
            });
        }

        // One line per section, not per item, and only when the BEST match is weak. If the top item
        // cleared the bar, the section has a direct answer and saying otherwise would teach the model
        // to hedge on evidence it should trust.
        var top = scored.Max(entry => entry.Score);
        if (top >= nearMissThreshold) return;

        state.AddBlock(
            ProjectedBlockKind.NoDirectMatch,
            sectionKey,
            $"No stored item directly matches this query (closest {sectionKey} match scored {top:F2}).");
    }
}
