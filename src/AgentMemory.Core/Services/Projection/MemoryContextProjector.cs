using AgentMemory.Abstractions.Domain;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// Runs the enabled projection features over one recalled context and materialises the result.
/// </summary>
/// <remarks>
/// <para>
/// <b>Returns null when nothing is enabled, and that null is the whole off-state guarantee.</b> A
/// non-null-but-empty <see cref="ProjectedContext"/> would still flow into three render surfaces and
/// make each of them take its projection-aware branch — the branch that must not execute for the
/// sealed prompt bytes to stay sealed. Null short-circuits all three before any of that.
/// </para>
/// <para>
/// It also returns null when features ran and contributed nothing. That case is genuinely
/// indistinguishable from "off" at the prompt — both render identically — and returning an empty
/// projection instead would put every surface on its new code path to produce byte-identical output,
/// which is a risk with no benefit.
/// </para>
/// </remarks>
internal sealed class MemoryContextProjector(IEnumerable<IProjectionFeature> features)
{
    private readonly IReadOnlyList<IProjectionFeature> _features = [.. features];

    /// <summary>The registered features, in execution order. Exposed for the reachability guard.</summary>
    internal IReadOnlyList<IProjectionFeature> Features => _features;

    public async Task<ProjectedContext?> ProjectAsync(
        ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var enabled = _features.Where(feature => feature.IsEnabled(state.Options)).ToList();
        if (enabled.Count == 0) return null;

        foreach (var feature in enabled)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await feature.ApplyAsync(state, cancellationToken).ConfigureAwait(false);
        }

        return state.IsEmpty ? null : state.Build();
    }
}
