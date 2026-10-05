using System.Globalization;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.AgentFramework.Recall;
using AgentMemory.Core.Routing;
using AgentMemory.Validation.Routing;
using Microsoft.Extensions.AI;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// <c>routing-score --set &lt;file&gt; [--split dev|heldout|all] [--policy today|everything|rules]</c> (root PLAN 40.61):
/// scores a routing policy on a frozen routing set. No store, no model. <c>today</c> is what an Agent Framework host runs
/// by default (40.67: <see cref="TrivialTurnRecallPolicy"/> narrows a greeting or a thanks to the recent messages, every
/// other turn reads every kind); <c>everything</c> is recall with no policy (every kind on every turn: MCP, Semantic
/// Kernel and direct callers); <c>rules</c> is the core router's default rules (40.56).
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
        // today: an Agent Framework host's default; everything: recall with no policy; rules: the core router (40.56).
        Func<RoutingItem, IReadOnlySet<string>>? scored = chosen switch
        {
            "today" => Today(set),
            "everything" => RoutingScorer.Everything(set),
            "rules" => RoutingScorer.FromRouter(new RuleBasedMemoryRouter(new MemoryRoutingOptions())),
            _ => null,
        };
        if (scored is null)
        {
            output.WriteLine($"error: --policy '{chosen}' is not today, everything or rules.");
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

    /// <summary>
    /// Today's default in an Agent Framework host (40.67): <see cref="TrivialTurnRecallPolicy"/>. A greeting or a thanks is
    /// narrowed to the recent messages, which are not a routed kind (so it reads none); every other turn reads them all.
    /// </summary>
    public static Func<RoutingItem, IReadOnlySet<string>> Today(RoutingSet set)
    {
        var everything = RoutingScorer.Everything(set);
        IReadOnlySet<string> none = new HashSet<string>(StringComparer.Ordinal);
        var policy = new TrivialTurnRecallPolicy();
        return item =>
        {
            var decision = policy.DecideAsync(new AutomaticRecallContext
            {
                Messages = [new ChatMessage(ChatRole.User, item.Text)],
                SessionId = "routing-score",
                ConversationId = "routing-score",
            }).AsTask().GetAwaiter().GetResult();
            return decision.ShouldRecall && decision.Categories != AutomaticRecallCategories.RecentMessages ? everything(item) : none;
        };
    }
}
