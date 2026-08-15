using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// Renders what a fact used to say, using the supersession edges live recall filters away.
/// </summary>
/// <remarks>
/// <para>
/// <b>The loss this closes.</b> Live fact recall filters <c>invalidated_at IS NULL</c>, so a
/// superseded fact is not "shown as old" — it is <i>absent</i>. A knowledge-update question therefore
/// arrives with the current answer and no cue that the answer ever changed, while the graph holds the
/// <c>SUPERSEDED_BY</c> edge that says exactly that. Knowledge-update is one of the weakest measured
/// non-episodic types.
/// </para>
/// <para>
/// <b>Exactly one extra read per recall, and it is enforced by test.</b> One batched query for the
/// whole fact section, anchored on ids already retrieved. Off ⇒ the repository is never touched, which
/// is also asserted — a feature that reads when disabled is a latency cost nobody opted into.
/// </para>
/// </remarks>
internal sealed class SupersessionProjectionFeature(IFactRepository facts) : IProjectionFeature
{
    public bool IsEnabled(MemoryProjectionOptions options) => options.ResolveSupersessions;

    public async Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var factIds = state.Facts.Select(fact => fact.FactId).ToList();
        if (factIds.Count == 0) return;

        var predecessors = await facts.GetSupersessionPredecessorsAsync(
            factIds, state.Options.MaxSupersessionChain, cancellationToken).ConfigureAwait(false);

        foreach (var fact in state.Facts)
        {
            if (!predecessors.TryGetValue(fact.FactId, out var chain) || chain.Count == 0) continue;

            var note = Render(chain);
            if (note is null) continue;

            state.Annotate(fact.FactId, annotation => annotation with { SupersessionNote = note });
        }
    }

    /// <summary>
    /// Builds <c>"(since 2023-05-12; previously Globex)"</c>, extending with <c>"; earlier …"</c>.
    /// </summary>
    /// <remarks>
    /// The date comes from the <b>most recent</b> predecessor's close, because that is the instant the
    /// current value took over — the reader's "since when?" is about the current fact, not about the
    /// oldest thing in the chain. Where a predecessor was never stamped, the date is simply omitted
    /// rather than guessed: a fabricated date in a temporal cue is worse than no cue.
    /// </remarks>
    private static string? Render(IReadOnlyList<SupersededFact> chain)
    {
        var newest = chain[0];
        var previous = chain
            .Select(entry => entry.Object)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        if (previous.Count == 0) return null;

        var since = newest.EffectiveDate is { } date
            ? $"since {date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}; "
            : string.Empty;

        var earlier = previous.Count == 1
            ? previous[0]
            : previous[0] + string.Concat(previous.Skip(1).Select(value => $"; earlier {value}"));

        return $"({since}previously {earlier})";
    }
}
