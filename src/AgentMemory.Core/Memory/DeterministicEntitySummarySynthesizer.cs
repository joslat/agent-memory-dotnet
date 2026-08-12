using System.Globalization;
using System.Text;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Memory;

/// <summary>
/// Composes an entity summary from its facts without a model call (S1).
/// </summary>
/// <remarks>
/// <para>
/// The shipped default, and deliberately not an LLM. A summary is invalidated whenever any of its
/// sources moves, so it is regenerated often; a completion per entity per change would make the
/// feature cost scale with how much the conversation talks about its subjects — which is exactly the
/// entities that get summarised most.
/// </para>
/// <para>
/// It is also reproducible. Two runs over the same facts produce the same bytes, so a summary can be
/// compared, fingerprinted and diffed, and a change in the text always means a change in the facts
/// rather than a change in a sampler's mood.
/// </para>
/// <para>
/// The output is a grouped rendering, not prose: predicates gathered with their objects, ordered
/// deterministically, most-confident first within each predicate. Honest about what it is — a
/// compact projection of the graph, which is what a caller needs when the alternative is twenty
/// separate facts each spending context to say one thing.
/// </para>
/// </remarks>
internal sealed class DeterministicEntitySummarySynthesizer : IEntitySummarySynthesizer
{
    /// <summary>Facts below this confidence are left out of the summary.</summary>
    /// <remarks>
    /// A summary states things flatly, with none of the per-fact confidence a caller would otherwise
    /// see. Folding a 0.2-confidence guess into that sentence would present it with the same
    /// authority as everything else and remove the only signal that it was doubtful.
    /// </remarks>
    public double MinimumConfidence { get; init; } = 0.5;

    /// <inheritdoc/>
    public Task<string?> SynthesizeAsync(
        Entity entity,
        IReadOnlyList<Fact> facts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(facts);

        var usable = facts
            .Where(f => f.Confidence >= MinimumConfidence)
            .Where(f => !string.IsNullOrWhiteSpace(f.Predicate) && !string.IsNullOrWhiteSpace(f.Object))
            .ToList();

        // Null, not an empty summary. "Nothing is known about this entity above the confidence floor"
        // is a real answer, and storing it as an empty string would create a node that satisfies every
        // has-a-summary check while saying nothing.
        if (usable.Count == 0) return Task.FromResult<string?>(null);

        var builder = new StringBuilder();
        builder.Append(entity.Name);
        if (!string.IsNullOrWhiteSpace(entity.Type))
            builder.Append(" (").Append(entity.Type).Append(')');
        builder.Append(':');

        var grouped = usable
            .GroupBy(f => f.Predicate, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase);

        foreach (var group in grouped)
        {
            var objects = group
                // Most-confident first, then alphabetical: confidence alone leaves ties resolved by
                // whatever order the store happened to return, which would make the text -- and so its
                // fingerprint -- vary between identical runs.
                .OrderByDescending(f => f.Confidence)
                .ThenBy(f => f.Object, StringComparer.OrdinalIgnoreCase)
                .Select(f => f.Object)
                .Distinct(StringComparer.OrdinalIgnoreCase);

            builder.Append(string.Create(
                CultureInfo.InvariantCulture,
                $"\n- {group.Key}: {string.Join("; ", objects)}"));
        }

        return Task.FromResult<string?>(builder.ToString());
    }
}
