using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>One piece of advice about a loaded pack: the rule, the pack it is about, and what to do. Advice never refuses a pack.</summary>
[Experimental("AMEXT001")]
public sealed record PackAdviceItem(string Rule, string Pack, string Text);

/// <summary>
/// PACK-composer: advice on a pack that verifies and loads, about what a host will do with it later rather than whether it
/// is valid now (<c>ampack lint</c> checks validity). A pack with no trial is refused by a host that requires trials; a
/// trial with no upgrade rule lets a later version do worse and still go on; links into another pack's types need that
/// pack's consent; a pack about to stop being valid will be switched off. Each rule is one the format enforces somewhere.
/// </summary>
[Experimental("AMEXT001")]
public static class PackAdvice
{
    /// <summary>Fewer trial tasks than this give a 95% bound too wide to tell a good pack from chance.</summary>
    public const int FewTasks = 30;
    /// <summary>A pack that stops being valid within this many days is flagged.</summary>
    public const int SoonDays = 30;

    /// <summary>Advice on every pack an inspection loaded, in order.</summary>
    public static IReadOnlyList<PackAdviceItem> Of(PackInspection inspection, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        IEnumerable<LoadedPack> packs = inspection.Pack is { } single ? [single]
            : inspection.Set is { } set ? [.. set.Families.SelectMany(f => f.Members), .. set.Packs] : [];
        return [.. packs.SelectMany(p => Of(p, now))];
    }

    /// <summary>Advice on one loaded pack.</summary>
    public static IReadOnlyList<PackAdviceItem> Of(LoadedPack pack, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var advice = new List<PackAdviceItem>();
        void Add(string rule, string text) => advice.Add(new(rule, pack.Id, text));
        var manifest = pack.Manifest;
        // A snapshot is measured through its definition; a set or composite through its members.
        if (pack.Kind is "knowledge" or "definition")
        {
            if (manifest.Trial is not { } trial)
                Add("trial.missing", "It declares no trial: a host that requires trials for its namespace refuses it, and nothing measures it on that host's history.");
            else
            {
                if (trial.Upgrade is not { NoRegression: true })
                    Add("trial.upgrade", "Its trial has no upgrade rule: unless an earlier version set one on that host, a later version could do worse than this one and still go on. Declare upgrade.noRegression.");
                if (trial.Tasks < FewTasks)
                    Add("trial.small", $"Its trial runs {trial.Tasks} tasks: a 95% bound on so few is wide; {FewTasks} or more tell a good pack from chance.");
            }
        }
        if (pack.Kind == "knowledge" && string.IsNullOrWhiteSpace(manifest.Display?.Description))
            Add("display.description", "It has no description: hosts and composers can show only its id.");
        if (manifest.Traversal is { } walk && walk.Anchors.Count == 0)
            Add("walk.anchors", "Its walk declares no anchors: it starts only where another pack's walk reaches it.");
        foreach (var module in (manifest.Ontology?.Relations ?? []).Select(r => r.To).Where(t => t.Contains(':', StringComparison.Ordinal))
            .Select(t => t[..t.IndexOf(':', StringComparison.Ordinal)]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            Add("links.consent", $"It links into {module}: a host follows those links only if {module} consents (containableBy) or the host grants the pair.");
        if (pack.ValidUntil is { } until && until - now < TimeSpan.FromDays(SoonDays))
            Add("validity.soon", until <= now
                ? $"It stopped being valid here on {Day(until)}."
                : $"It stops being valid here on {Day(until)}, in {Math.Max(1, (int)Math.Ceiling((until - now).TotalDays))} days: its own date, its set's, the trust's or a signing key's, whichever is first.");
        return advice;
    }

    private static string Day(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
