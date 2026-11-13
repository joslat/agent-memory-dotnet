using System.Globalization;
using System.Text.Json;

namespace AgentMemory.Scenarios.PublicGold;

/// <summary>
/// Wrong merges and splits of a store's identities against an identity answer key. A C# port of the strategy helper
/// entity-store-metrics.py (same rules, same numbers on the same report), plus what the public gold needs: word-prefix
/// matching, typeless groups, pending proposals counted apart, and event identities that span two similar events.
/// </summary>
/// <remarks>
/// Two views, scored with the same rules:
/// <list type="bullet">
/// <item><see cref="DecisionsView"/> (evidence-based identity): an identity is a decision's confirmed target, otherwise
/// its own observation.</item>
/// <item><see cref="EntitiesView"/> (old model): an identity is a live Entity node; its mentions are its name and aliases,
/// attributed to each statement it was extracted from. SAME_AS links are counted, never treated as merges.</item>
/// </list>
/// A mention is labelled with a gold group only when its words match that group's keywords, its type matches the group's
/// kind and its source statement mentions that group, and exactly one group does; otherwise it stays unlabelled, never
/// scored. False merge: one identity holding mentions of two or more gold groups. Split: one gold group across several
/// identities.
/// </remarks>
internal static class IdentityGoldScorer
{
    public const string DecisionsView = "decisions";
    public const string EntitiesView = "entities";

    private static readonly Dictionary<string, string> TypeKind = new(StringComparer.Ordinal)
    {
        ["PLACE"] = "LOCATION", ["THING"] = "OBJECT", ["ORGANISATION"] = "ORGANIZATION",
    };

    private sealed record Mention(string Identity, string AcceptedIdentity, string? Group, string Words, string Kind, IReadOnlyList<int> Statements);

    public static IdentityGoldScore Score(IdentityGold gold, IReadOnlyList<string> statements, JsonElement store, string view)
    {
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < statements.Count; i++)
            (index.TryGetValue(statements[i], out var at) ? at : index[statements[i]] = []).Add(i + 1);
        int[] Numbers(IEnumerable<string?> texts) =>
            texts.SelectMany(text => text is not null && index.TryGetValue(text, out var at) ? at : []).Distinct().Order().ToArray();

        var mentions = new List<Mention>();
        var sameAs = 0;
        var pending = 0;
        if (view == DecisionsView)
        {
            foreach (var decision in store.GetProperty("identity").GetProperty("decisions").EnumerateArray())
            {
                var observation = decision.GetProperty("Observation");
                var kind = Kind(observation.GetProperty("Type").GetString());
                var context = decision.GetProperty("Context");
                var sourceIds = context.GetProperty("SourceMessageIds").EnumerateArray().Select(id => id.GetString()).ToHashSet(StringComparer.Ordinal);
                var said = Numbers(context.GetProperty("Sources").EnumerateArray()
                    .Where(source => sourceIds.Contains(source.GetProperty("SourceId").GetString()))
                    .Select(source => source.GetProperty("Text").GetString()));
                var own = observation.GetProperty("EntityId").GetString() ?? "";
                var status = decision.GetProperty("Status").GetInt32();
                var identity = status == 0 && decision.TryGetProperty("ConfirmedEntityId", out var confirmed) && confirmed.ValueKind == JsonValueKind.String
                    ? confirmed.GetString()! : own;
                var proposals = decision.TryGetProperty("PendingEntityIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                    ? ids.EnumerateArray().Select(id => id.GetString()!).ToArray() : [];
                if (status == 1) pending++;
                var name = observation.GetProperty("Name").GetString() ?? "";
                mentions.Add(new(identity, status == 1 && proposals.Length == 1 ? proposals[0] : identity, Label(gold, name, kind, said), name, kind, said));
            }
        }
        else if (view == EntitiesView)
        {
            var snapshot = store.GetProperty("entities");
            foreach (var entity in snapshot.GetProperty("entities").EnumerateArray())
            {
                var kind = Kind(entity.GetProperty("type").GetString());
                var id = entity.GetProperty("id").GetString() ?? "";
                var said = Numbers(entity.GetProperty("sources").EnumerateArray().Select(text => text.GetString()));
                var words = new[] { entity.GetProperty("name").GetString() ?? "" }
                    .Concat(entity.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString() ?? ""));
                foreach (var word in words)
                {
                    if (said.Length == 0) mentions.Add(new(id, id, null, word, kind, []));
                    foreach (var statement in said) mentions.Add(new(id, id, Label(gold, word, kind, [statement]), word, kind, [statement]));
                }
            }
            sameAs = snapshot.GetProperty("sameAs").GetArrayLength();
        }
        else
        {
            throw new ArgumentException($"Unknown view '{view}'; use '{DecisionsView}' or '{EntitiesView}'.", nameof(view));
        }

        var labelled = mentions.Where(m => m.Group is not null).ToList();
        var groupsByIdentity = labelled.GroupBy(m => m.Identity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(m => m.Group!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var merges = groupsByIdentity.Where(pair => pair.Value.Count > 1)
            .ToDictionary(pair => pair.Key, pair => (IReadOnlyList<string>)pair.Value, StringComparer.Ordinal);
        var splits = Splits(labelled, m => m.Identity);
        var acceptedSplits = Splits(labelled, m => m.AcceptedIdentity);
        string KindOf(string group) => gold.Groups[group].Kind;

        return new IdentityGoldScore(
            view,
            mentions.Select(m => m.Identity).Distinct(StringComparer.Ordinal).Count(),
            merges.Count,
            merges,
            splits.Values.Sum(n => n - 1),
            splits.Where(pair => KindOf(pair.Key) == "EVENT").Sum(pair => pair.Value - 1),
            splits.Where(pair => KindOf(pair.Key) is "PERSON" or "ORGANIZATION").Sum(pair => pair.Value - 1),
            splits,
            sameAs,
            labelled.Where(m => m.Group == "GO-LIVE").Select(m => m.Identity).Distinct(StringComparer.Ordinal).Count(),
            labelled.Count,
            mentions.Count - labelled.Count,
            pending,
            acceptedSplits.Values.Sum(n => n - 1),
            CrossPartition(gold, mentions));
    }

    /// <summary>The one gold group these words name in these statements, or null (no group, or more than one).</summary>
    public static string? Label(IdentityGold gold, string words, string kind, IReadOnlyCollection<int> statements)
    {
        if (statements.Count == 0) return null;
        var allowed = statements
            .SelectMany(n => gold.StatementGroups.TryGetValue(n.ToString(CultureInfo.InvariantCulture), out var groups) ? groups : [])
            .ToHashSet(StringComparer.Ordinal);
        var lowered = words.ToLowerInvariant();
        string? hit = null;
        var hits = 0;
        foreach (var id in allowed)
        {
            if (!gold.Groups.TryGetValue(id, out var group)) continue;
            if (group.Kind != kind && group.Kind != IdentityGold.AnyKind) continue;
            if (!group.Keywords.Any(keyword => Matches(gold.Match, lowered, keyword.ToLowerInvariant()))) continue;
            hits++;
            hit = id;
        }

        return hits == 1 ? hit : null;
    }

    private static bool Matches(string match, string lowered, string keyword)
    {
        if (keyword.Length == 0) return false;
        if (match != IdentityGold.WordPrefix) return lowered.Contains(keyword, StringComparison.Ordinal);
        for (var at = lowered.IndexOf(keyword, StringComparison.Ordinal); at >= 0; at = lowered.IndexOf(keyword, at + 1, StringComparison.Ordinal))
            if (at == 0 || !char.IsLetterOrDigit(lowered[at - 1])) return true;
        return false;
    }

    private static string Kind(string? type)
    {
        var upper = (type ?? "").ToUpperInvariant();
        return TypeKind.GetValueOrDefault(upper, upper);
    }

    private static Dictionary<string, int> Splits(IEnumerable<Mention> labelled, Func<Mention, string> identity) =>
        labelled.GroupBy(m => m.Group!, StringComparer.Ordinal)
            .Select(g => (g.Key, Count: g.Select(identity).Distinct(StringComparer.Ordinal).Count()))
            .Where(pair => pair.Count > 1)
            .ToDictionary(pair => pair.Key, pair => pair.Count, StringComparer.Ordinal);

    // An event identity whose statements come from two subtopics of one ECB+ topic joins two similar but different real
    // events (or recognised a generic one): listed for a person to read, whatever the keyword labels say.
    private static List<CrossPartitionIdentity> CrossPartition(IdentityGold gold, IEnumerable<Mention> mentions)
    {
        if (gold.StatementPartitions is not { } partitions) return [];
        var crossed = new List<CrossPartitionIdentity>();
        foreach (var identity in mentions.Where(m => m.Kind == "EVENT").GroupBy(m => m.Identity, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var spanned = identity.SelectMany(m => m.Statements)
                .Select(n => partitions.TryGetValue(n.ToString(CultureInfo.InvariantCulture), out var p) ? p : null)
                .OfType<string>().Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            if (spanned.GroupBy(p => p.Split('/')[0], StringComparer.Ordinal).Any(topic => topic.Count() > 1))
                crossed.Add(new(identity.Key, identity.Select(m => m.Words).Distinct(StringComparer.Ordinal).ToList(), spanned));
        }

        return crossed;
    }
}

/// <summary>What <see cref="IdentityGoldScorer"/> found in one store.</summary>
/// <param name="View">The identity view scored.</param>
/// <param name="Identities">Distinct identities among the mentions.</param>
/// <param name="FalseMerges">Identities holding mentions of two or more gold groups.</param>
/// <param name="FalseMergeDetail">Each merged identity and the groups it holds.</param>
/// <param name="ExtraIdentities">Splits: for each gold group, identities beyond the first.</param>
/// <param name="EventExtraIdentities">Splits of EVENT groups.</param>
/// <param name="PersonOrgExtraIdentities">Splits of PERSON and ORGANIZATION groups.</param>
/// <param name="SplitGroups">Each split group and how many identities hold it.</param>
/// <param name="SameAsLinks">SAME_AS links (old model), counted, never merges.</param>
/// <param name="GoLiveIdentities">Bariloche parity: identities of the GO-LIVE group (0 for other gold).</param>
/// <param name="LabelledMentions">Mentions labelled with exactly one gold group.</param>
/// <param name="UnlabelledMentions">Mentions matching no group, or several; never scored.</param>
/// <param name="PendingDecisions">Decisions that keep a reviewable proposal (decisions view).</param>
/// <param name="ExtraIdentitiesIfPendingAccepted">Splits if every single-target proposal were accepted.</param>
/// <param name="CrossPartitionEventIdentities">Event identities across two similar events of one topic (ECB+).</param>
internal sealed record IdentityGoldScore(
    string View,
    int Identities,
    int FalseMerges,
    IReadOnlyDictionary<string, IReadOnlyList<string>> FalseMergeDetail,
    int ExtraIdentities,
    int EventExtraIdentities,
    int PersonOrgExtraIdentities,
    IReadOnlyDictionary<string, int> SplitGroups,
    int SameAsLinks,
    int GoLiveIdentities,
    int LabelledMentions,
    int UnlabelledMentions,
    int PendingDecisions,
    int ExtraIdentitiesIfPendingAccepted,
    IReadOnlyList<CrossPartitionIdentity> CrossPartitionEventIdentities);

/// <summary>An event identity whose statements span two subtopics of one topic, with its names.</summary>
internal sealed record CrossPartitionIdentity(string Identity, IReadOnlyList<string> Names, IReadOnlyList<string> Partitions);
