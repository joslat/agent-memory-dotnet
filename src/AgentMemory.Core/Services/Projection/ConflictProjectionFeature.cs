using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Services.Projection;

/// <summary>
/// Says so when two <b>live recalled</b> facts contradict each other, instead of letting them sit
/// apart in the prompt as if both were simply true.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why in-context grouping and not the conflict-detection service.</b>
/// <c>IConflictDetectionService</c> shipped detect-only with no read-path consumer, and calling it per
/// recall would mean a full-store scan on every turn. The grouping semantics used here are the ones it
/// documents — same subject and predicate, same owner, two or more distinct objects, all live — but
/// applied to the recalled set, which is O(items) and covers exactly the case that can mislead: a
/// conflict whose members are both <i>in the prompt</i>. A contradiction the model never sees cannot
/// mislead it, and resolving it durably is <c>ResolveFactContradictionsAsync</c>'s job, not the
/// renderer's.
/// </para>
/// <para>
/// <b>Owner-bucketed.</b> Two owners asserting different values for the same subject and predicate is
/// not a contradiction — it is two tenants — and rendering it as one would leak the existence of the
/// other owner's data into this owner's prompt.
/// </para>
/// <para>
/// Pure: no I/O, no extra round trip.
/// </para>
/// </remarks>
internal sealed class ConflictProjectionFeature : IProjectionFeature
{
    /// <remarks>
    /// ⚠️ <b>OBSERVABILITY GAP, recorded rather than half-closed.</b> When this feature is enabled
    /// and renders nothing, there is no way to tell "ran and found no conflicting claims" from
    /// "never ran" — the shape this codebase has found on several paths. Closing it properly means
    /// either injecting a logger (no projection feature takes one today, so it is a DI change) or
    /// having <c>ProjectionState</c> record which features applied, which is the better general fix
    /// and larger than a drive-by. Left whole and documented instead of partly wired: a half-wired
    /// read side cost this project a five-hour measurement, and the lesson is that an honest gap
    /// beats a knob that looks like a fix.
    /// <para>
    /// The sibling gap on <c>LegibleForgetting</c> WAS closed, because it collapsed five distinct
    /// outcomes into one empty list and needed only Debug lines on an object that already had a
    /// logger.
    /// </para>
    /// </remarks>
    public bool IsEnabled(MemoryProjectionOptions options) => options.RenderConflicts;

    public Task ApplyAsync(ProjectionState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);

        var groups = state.Facts
            // Only LIVE facts. A superseded one is history, not a competing claim -- and rendering it
            // as a conflict would contradict the supersession note the sibling feature attaches.
            .Where(fact => fact.InvalidatedAtUtc is null)
            .GroupBy(
                fact => (
                    Subject: MemoryTripleKey(fact.Subject),
                    Predicate: MemoryTripleKey(fact.Predicate),
                    Owner: fact.OwnerId ?? "*"),
                comparer: null);

        foreach (var group in groups)
        {
            var distinct = group
                .GroupBy(fact => MemoryTripleKey(fact.Object), StringComparer.Ordinal)
                .Select(objectGroup => objectGroup.First())
                .ToList();

            if (distinct.Count < 2) continue;

            var rendered = string.Join(
                " / ",
                distinct
                    .OrderByDescending(fact => fact.Confidence)
                    .ThenBy(fact => fact.FactId, StringComparer.Ordinal)
                    .Select(Describe));

            state.AddBlock(
                ProjectedBlockKind.ConflictingMemory,
                ProjectionSectionKeys.Facts,
                $"CONFLICTING MEMORY — {group.First().Subject} {group.First().Predicate}: {rendered}");
        }

        return Task.CompletedTask;
    }

    /// <summary>Value with its date, so the reader can prefer the newer claim rather than guess.</summary>
    private static string Describe(Fact fact)
    {
        var date = fact.ValidFrom ?? fact.CreatedAtUtc;
        return $"{fact.Object} ({date.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)})";
    }

    /// <summary>
    /// Case- and whitespace-insensitive grouping key.
    /// </summary>
    /// <remarks>
    /// Mirrors how the write path canonicalises a triple, so "Acme" and "acme " group together here
    /// exactly as they would collapse there. Grouping ordinally instead would report a conflict between
    /// two spellings of one answer — the most annoying possible false positive, since it would teach
    /// the model to hedge about something nobody disagrees on.
    /// </remarks>
    private static string MemoryTripleKey(string value) => value.Trim().ToUpperInvariant();
}
