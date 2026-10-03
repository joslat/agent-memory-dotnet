using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Core.Routing;
using AgentMemory.Validation.Routing;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// <c>routing-score --set &lt;file&gt; [--split dev|heldout|all] [--policy everything|rules]</c> (root PLAN 40.61): scores a
/// routing policy on a frozen routing set. No store, no model. <c>everything</c> is today's recall (every kind on every
/// turn), the baseline; <c>rules</c> is the core router's default rules (40.56).
/// </summary>
public sealed class RoutingScoreCommand(TextWriter output)
{
    public int Execute(string? setPath, string? split, string? policy, bool misses = false)
    {
        if (setPath is null)
        {
            output.WriteLine("error: routing-score needs --set <file>.");
            return 1;
        }
        split ??= "dev";
        if (split is not ("dev" or "heldout" or "all"))
        {
            output.WriteLine($"error: --split '{split}' is not dev, heldout or all.");
            return 1;
        }
        RoutingSet set;
        try
        {
            set = RoutingSet.Read(setPath);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            output.WriteLine($"error: routing-score: {ex.Message}");
            return 1;
        }
        var chosen = policy ?? "everything";
        // everything: today's recall. rules: the core router's default rules (40.56), what MemoryOptions.Routing turns on.
        Func<RoutingItem, IReadOnlySet<string>>? scored = chosen switch
        {
            "everything" => RoutingScorer.Everything(set),
            "rules" => RoutingScorer.FromRouter(new RuleBasedMemoryRouter(new MemoryRoutingOptions())),
            _ => null,
        };
        if (scored is null)
        {
            output.WriteLine($"error: --policy '{chosen}' is not everything or rules.");
            return 1;
        }
        if (split != "dev")
            output.WriteLine("note: the held-out split is for a frozen router only (40.61).");

        var score = RoutingScorer.Score(set, scored, split);
        var c = CultureInfo.InvariantCulture;
        output.WriteLine($"routing-score: policy {chosen}, split {split}, set sha256 {score.SetSha256}");
        output.WriteLine(string.Create(c, $"  items {score.Items}; needing memory {score.NeedingMemory}, fully served {score.FullySatisfied}; group recall {score.GroupRecall:0.000}"));
        output.WriteLine(string.Create(c, $"  reads per item {score.MeanReads:0.00}, wasted {score.MeanWastedReads:0.00}; silent when nothing is needed {score.SilentWhenNothingNeeded}/{score.NothingNeeded}"));
        foreach (var kind in score.PerKind)
            output.WriteLine($"  {kind.Kind,-12} read when needed {kind.ReadWhenNeeded}/{kind.Needed}, read when not needed {kind.ReadWhenNotNeeded}/{kind.NotNeeded}");
        if (misses)
        {
            // Each answer not fully served: what it needs and what the policy read.
            foreach (var item in set.Items.Where(i => split == "all" || i.Split == split))
            {
                var row = score.Rows.Single(r => r.Id == item.Id);
                if (row.Satisfied == row.Groups) continue;
                var needs = string.Join(" & ", item.Needs.Select(g => string.Join("|", g)));
                output.WriteLine($"  miss {item.Id}: needs {needs}, read {string.Join(",", scored(item).Order(StringComparer.Ordinal))} | {item.Text}");
            }
        }
        return 0;
    }
}
