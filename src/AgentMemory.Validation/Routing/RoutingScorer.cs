using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Validation.Routing;

/// <summary>
/// Scores a routing policy on a routing set, deterministically and with no model: which kinds it reads for each item,
/// against the kinds that hold the answer. Selection only; whether the routed context holds the gold memory (evidence
/// coverage) is the validation packs' check, run on a live store.
/// </summary>
public static class RoutingScorer
{
    /// <summary>Today's recall: every kind, every turn.</summary>
    public static Func<RoutingItem, IReadOnlySet<string>> Everything(RoutingSet set)
    {
        var all = new HashSet<string>(set.Kinds, StringComparer.Ordinal);
        return _ => all;
    }

    /// <summary>
    /// A memory router as a policy: the kinds it reads for the item's text. Shared knowledge is stored as facts and read
    /// with them (router design R3), so reading facts reads shared.
    /// </summary>
    public static Func<RoutingItem, IReadOnlySet<string>> FromRouter(IMemoryRouter router) => item =>
    {
        var route = router.Route(item.Text);
        var kinds = new HashSet<string>(route.Kinds, StringComparer.Ordinal);
        if (kinds.Contains(MemoryRoute.Facts)) kinds.Add("shared");
        return kinds;
    };

    /// <summary>Scores <paramref name="policy"/> on the items of <paramref name="split"/> (<c>dev</c>, <c>heldout</c> or <c>all</c>).</summary>
    public static RoutingScore Score(RoutingSet set, Func<RoutingItem, IReadOnlySet<string>> policy, string split = "dev")
    {
        var items = set.Items.Where(i => split == "all" || i.Split == split).ToList();
        var rows = items.Select(item =>
        {
            var read = policy(item);
            var satisfied = item.Needs.Count(group => group.Any(read.Contains));
            var useful = new HashSet<string>(item.Needs.SelectMany(g => g).Concat(item.Acceptable), StringComparer.Ordinal);
            return new RoutingRow(item.Id, item.Needs.Count, satisfied, read.Count, read.Count(k => !useful.Contains(k)));
        }).ToList();

        var needing = rows.Where(r => r.Groups > 0).ToList();
        var silent = rows.Where(r => r.Groups == 0).ToList();
        var perKind = set.Kinds.Select(kind =>
        {
            var needed = items.Where(i => i.Needs.Any(g => g.Contains(kind))).ToList();
            var notNeeded = items.Where(i => !i.Needs.Any(g => g.Contains(kind)) && !i.Acceptable.Contains(kind)).ToList();
            return new RoutingKindScore(kind,
                Needed: needed.Count,
                ReadWhenNeeded: needed.Count(i => policy(i).Contains(kind)),
                NotNeeded: notNeeded.Count,
                ReadWhenNotNeeded: notNeeded.Count(i => policy(i).Contains(kind)));
        }).ToList();

        return new RoutingScore(
            Split: split,
            SetSha256: set.Sha256,
            Items: rows.Count,
            NeedingMemory: needing.Count,
            FullySatisfied: needing.Count(r => r.Satisfied == r.Groups),
            GroupRecall: needing.Count == 0 ? 1 : needing.Average(r => (double)r.Satisfied / r.Groups),
            MeanReads: rows.Count == 0 ? 0 : rows.Average(r => r.Reads),
            MeanWastedReads: rows.Count == 0 ? 0 : rows.Average(r => r.Wasted),
            SilentWhenNothingNeeded: silent.Count(r => r.Reads == 0),
            NothingNeeded: silent.Count,
            PerKind: perKind,
            Rows: rows);
    }
}

/// <summary>A policy's score on one split.</summary>
/// <param name="Split">dev, heldout or all.</param>
/// <param name="SetSha256">The set's sha256, so a score names the frozen set it was taken on.</param>
/// <param name="Items">Items scored.</param>
/// <param name="NeedingMemory">Items whose answer needs memory.</param>
/// <param name="FullySatisfied">Of those, items where every needed group was read.</param>
/// <param name="GroupRecall">Mean share of needed groups read, over items needing memory.</param>
/// <param name="MeanReads">Mean kinds read per item.</param>
/// <param name="MeanWastedReads">Mean kinds read that neither hold nor may hold the answer.</param>
/// <param name="SilentWhenNothingNeeded">Items needing nothing on which nothing was read.</param>
/// <param name="NothingNeeded">Items needing nothing.</param>
/// <param name="PerKind">Per kind: how often read when needed and when not.</param>
/// <param name="Rows">Per item.</param>
public sealed record RoutingScore(
    string Split, string? SetSha256, int Items, int NeedingMemory, int FullySatisfied, double GroupRecall,
    double MeanReads, double MeanWastedReads, int SilentWhenNothingNeeded, int NothingNeeded,
    IReadOnlyList<RoutingKindScore> PerKind, IReadOnlyList<RoutingRow> Rows);

/// <summary>One kind's selection on a split.</summary>
public sealed record RoutingKindScore(string Kind, int Needed, int ReadWhenNeeded, int NotNeeded, int ReadWhenNotNeeded);

/// <summary>One item's selection.</summary>
public sealed record RoutingRow(string Id, int Groups, int Satisfied, int Reads, int Wasted);
