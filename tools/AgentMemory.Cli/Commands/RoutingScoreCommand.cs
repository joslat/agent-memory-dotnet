using System.Globalization;
using System.Text.Json;
using AgentMemory.Validation.Routing;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// <c>routing-score --set &lt;file&gt; [--split dev|heldout|all] [--policy everything]</c> (root PLAN 40.61): scores a routing
/// policy on a frozen routing set. No store, no model. Today the only policy is <c>everything</c> (what recall does now: every
/// kind on every turn), the baseline a router is measured against.
/// </summary>
public sealed class RoutingScoreCommand(TextWriter output)
{
    public int Execute(string? setPath, string? split, string? policy)
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
        if (chosen != "everything")
        {
            output.WriteLine($"error: --policy '{chosen}' is unknown; the router (40.56) is not built yet, so only 'everything' exists.");
            return 1;
        }
        if (split != "dev")
            output.WriteLine("note: the held-out split is for a frozen router only (40.61).");

        var score = RoutingScorer.Score(set, RoutingScorer.Everything(set), split);
        var c = CultureInfo.InvariantCulture;
        output.WriteLine($"routing-score: policy {chosen}, split {split}, set sha256 {score.SetSha256}");
        output.WriteLine(string.Create(c, $"  items {score.Items}; needing memory {score.NeedingMemory}, fully served {score.FullySatisfied}; group recall {score.GroupRecall:0.000}"));
        output.WriteLine(string.Create(c, $"  reads per item {score.MeanReads:0.00}, wasted {score.MeanWastedReads:0.00}; silent when nothing is needed {score.SilentWhenNothingNeeded}/{score.NothingNeeded}"));
        foreach (var kind in score.PerKind)
            output.WriteLine($"  {kind.Kind,-12} read when needed {kind.ReadWhenNeeded}/{kind.Needed}, read when not needed {kind.ReadWhenNotNeeded}/{kind.NotNeeded}");
        return 0;
    }
}
