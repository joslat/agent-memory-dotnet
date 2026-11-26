using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentMemory.Scenarios.PublicGold;

/// <summary>
/// The Identity Kinds Benchmark, tier 2 (IKB-2): what a store made of a conversation's real things, against the answer
/// key, with no model judging. A stored thing is mapped to real things through the statements its mentions came from:
/// each mention of an identity, in one statement, is labelled with the one real thing of that statement it names.
/// </summary>
/// <remarks>
/// <para>The two views of <see cref="IdentityGoldScorer"/>: <see cref="IdentityGoldScorer.EntitiesView"/> for the old
/// resolver (an identity is a live Entity; its words are its name and aliases, in every statement it came from) and
/// <see cref="IdentityGoldScorer.DecisionsView"/> for evidence-based identity (an identity is a decision's confirmed
/// target, otherwise its own observation).</para>
/// <para>Labelling, among the real things the key mentions in that statement: a thing whose name shares a word with the
/// mention's words and whose type the mention has; failing that, the one thing whose name shares a word, whatever the
/// type (a model may type a dog a person); failing that, the one thing of the mention's type. Anything else, or two
/// equally good things, stays unlabelled and is listed, never scored. Names are a thing's description up to its first
/// comma or parenthesis and the names it quotes, without the owner's name and small words.</para>
/// <para>A wrong merge is an identity holding two or more real things. A split is a real thing held by more than one
/// identity. A real thing is right when exactly one identity holds it and holds nothing else.</para>
/// </remarks>
internal static class IdentityKindsScorer
{
    private static readonly HashSet<string> SmallWords = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "in", "at", "for", "and", "to", "on", "other", "own", "my", "her", "his", "their", "its", "also",
    };

    private static readonly Dictionary<string, string> TypeKind = new(StringComparer.Ordinal)
    {
        ["PLACE"] = "LOCATION", ["THING"] = "OBJECT", ["ORGANISATION"] = "ORGANIZATION",
    };

    private sealed record Observation(string Identity, string Words, string Type, int Statement);

    /// <summary>ER-KIT: why a real thing's extra identity was not joined to its first (h2h/split_causes.py, kept here).</summary>
    internal static class SplitCause
    {
        /// <summary>The extra identity has another type than the first (both mapped to the key's five types).</summary>
        public const string TypeChanged = "type-changed";
        /// <summary>No name word in common with the first: a nickname, a role, a new name.</summary>
        public const string OtherName = "other-name";
        /// <summary>The same name words, its decision left Pending for review.</summary>
        public const string PendingRepeat = "pending-repeat";
        /// <summary>The same name words, decided New (no admissible candidate) or joined to an earlier duplicate.</summary>
        public const string NewRepeat = "new-repeat";
        /// <summary>The same name words, in a store without identity decisions (the old resolver's entities).</summary>
        public const string SameName = "same-name";
    }

    public static IdentityKindsScore Score(IdentityKindsGold gold, Scenario scenario, JsonElement store, string view)
    {
        var statements = scenario.Turns().Select(t => t.Text).ToList();
        var index = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (var i = 0; i < statements.Count; i++)
            (index.TryGetValue(statements[i], out var at) ? at : index[statements[i]] = []).Add(i + 1);
        int[] Numbers(IEnumerable<string?> texts) =>
            texts.SelectMany(text => text is not null && index.TryGetValue(text, out var at) ? at : []).Distinct().Order().ToArray();

        var owner = Words(scenario.OwnerName ?? "");
        var names = gold.Things.ToDictionary(t => t.Id, t => IdentityKindsGold.NamesOf(t).SelectMany(NameWords).Where(w => !owner.Contains(w)).ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var things = gold.Things.ToDictionary(t => t.Id, StringComparer.Ordinal);
        var inStatement = gold.Mentions.GroupBy(m => m.Statement).ToDictionary(g => g.Key, g => g.Select(m => m.Thing).Distinct(StringComparer.Ordinal).ToList());

        var observations = new List<Observation>();
        var pending = 0;
        var unplaced = 0;
        // ER-KIT split causes: the status of each identity's first decision, in store order (0 Confirmed, 1 Pending, 2 New).
        var firstStatus = new Dictionary<string, int>(StringComparer.Ordinal);
        if (view == IdentityGoldScorer.DecisionsView)
        {
            foreach (var decision in store.GetProperty("identity").GetProperty("decisions").EnumerateArray())
            {
                var observation = decision.GetProperty("Observation");
                var context = decision.GetProperty("Context");
                var sourceIds = context.GetProperty("SourceMessageIds").EnumerateArray().Select(id => id.GetString()).ToHashSet(StringComparer.Ordinal);
                var said = Numbers(context.GetProperty("Sources").EnumerateArray()
                    .Where(source => sourceIds.Contains(source.GetProperty("SourceId").GetString()))
                    .Select(source => source.GetProperty("Text").GetString()));
                var status = decision.GetProperty("Status").GetInt32();
                if (status == 1) pending++;
                var identity = status == 0 && decision.TryGetProperty("ConfirmedEntityId", out var confirmed) && confirmed.ValueKind == JsonValueKind.String
                    ? confirmed.GetString()! : observation.GetProperty("EntityId").GetString() ?? "";
                var name = observation.GetProperty("Name").GetString() ?? "";
                var type = observation.GetProperty("Type").GetString() ?? "";
                firstStatus.TryAdd(identity, status);
                if (said.Length == 0) unplaced++;
                observations.AddRange(said.Select(n => new Observation(identity, name, type, n)));
            }
        }
        else if (view == IdentityGoldScorer.EntitiesView)
        {
            foreach (var entity in store.GetProperty("entities").GetProperty("entities").EnumerateArray())
            {
                var id = entity.GetProperty("id").GetString() ?? "";
                var type = entity.GetProperty("type").GetString() ?? "";
                var said = Numbers(entity.GetProperty("sources").EnumerateArray().Select(text => text.GetString()));
                var words = string.Join(" ", new[] { entity.GetProperty("name").GetString() ?? "" }
                    .Concat(entity.GetProperty("aliases").EnumerateArray().Select(alias => alias.GetString() ?? "")));
                if (said.Length == 0) unplaced++;
                observations.AddRange(said.Select(n => new Observation(id, words, type, n)));
            }
        }
        else
        {
            throw new ArgumentException($"Unknown view '{view}'.", nameof(view));
        }

        var labels = observations.Select(o =>
        {
            var (thing, how) = Label(o, inStatement.GetValueOrDefault(o.Statement) ?? [], things, names);
            return new LabelledMention(o.Identity, o.Words, o.Type, o.Statement, thing, how);
        }).ToList();
        var labelled = labels.Where(l => l.Thing is not null).ToList();

        var thingsByIdentity = labelled.GroupBy(l => l.Identity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(l => l.Thing!).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var identitiesByThing = gold.Things.ToDictionary(t => t.Id,
            t => labelled.Where(l => l.Thing == t.Id).Select(l => l.Identity).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(), StringComparer.Ordinal);
        var merges = thingsByIdentity.Where(p => p.Value.Count > 1)
            .ToDictionary(p => p.Key, p => (IReadOnlyList<string>)p.Value, StringComparer.Ordinal);
        var merged = merges.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);
        bool Right(string thing) => identitiesByThing[thing] is [var only] && thingsByIdentity[only].Count == 1;

        var byKind = gold.Things.GroupBy(t => t.Kind, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new IdentityKindsTally(
                g.Count(),
                g.Count(t => Right(t.Id)),
                g.Sum(t => Math.Max(0, identitiesByThing[t.Id].Count - 1)),
                g.Count(t => merged.Contains(t.Id)),
                g.Count(t => identitiesByThing[t.Id].Count == 0)), StringComparer.Ordinal);
        var byLevel = gold.Mentions.GroupBy(m => m.Level).OrderBy(g => g.Key)
            .ToDictionary(g => g.Key.ToString(CultureInfo.InvariantCulture), g => new IdentityKindsLevel(
                g.Count(),
                g.Count(m => labelled.Any(l => l.Statement == m.Statement && l.Thing == m.Thing)),
                g.Count(m => Right(m.Thing) && labelled.Any(l => l.Statement == m.Statement && l.Thing == m.Thing))), StringComparer.Ordinal);

        // ER-KIT: the field's clustering scores over the labelled mentions, the types the store gave each real thing, the
        // identities that hold no thing of the key, and why each split's extra identities were not joined.
        var clustering = Clustering(labelled);
        var typing = gold.Things.Where(t => identitiesByThing[t.Id].Count > 0).GroupBy(t => t.Kind, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new IdentityKindsTyping(g.Count(),
                g.Count(t => labelled.Where(l => l.Thing == t.Id).All(l => Kind(l.Type) == t.Type))), StringComparer.Ordinal);
        var holding = labelled.Select(l => l.Identity).ToHashSet(StringComparer.Ordinal);
        var outside = labels.Where(l => !holding.Contains(l.Identity)).GroupBy(l => l.Identity, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Words, StringComparer.Ordinal);
        var splitCauses = SplitCauses(gold, labelled, identitiesByThing, firstStatus, view);
        var attribution = view == IdentityGoldScorer.DecisionsView ? FactAttribution(store, Numbers, inStatement, thingsByIdentity) : null;

        return new IdentityKindsScore(
            view,
            gold.Things.Count,
            gold.Things.Count(t => Right(t.Id)),
            merges.Count,
            merges,
            identitiesByThing.Values.Sum(v => Math.Max(0, v.Count - 1)),
            identitiesByThing.Where(p => p.Value.Count > 1).ToDictionary(p => p.Key, p => p.Value.Count, StringComparer.Ordinal),
            gold.Things.Count(t => identitiesByThing[t.Id].Count == 0),
            byKind,
            byLevel,
            pending,
            labelled.Count,
            labels.Count - labelled.Count,
            unplaced,
            labels)
        {
            Clustering = clustering,
            TypeByKind = typing,
            IdentitiesOutsideTheKey = outside,
            SplitCauses = splitCauses.Causes,
            SplitCausesByKind = splitCauses.ByKind,
            FactAttribution = attribution,
        };
    }

    /// <summary>
    /// ER-KIT fact attribution: each fact's subject or object bound to an identity (the store's identity bindings) against the
    /// real things the fact's own statements mention. Right: the identity holds one real thing and the statement mentions
    /// it. Wrong: it holds one real thing the statement does not mention (a namesake's, say). Merged: it holds several.
    /// Unscored: the identity holds no thing of the key, or the fact has no source statement (reports before ER-KIT).
    /// </summary>
    private static IdentityKindsFactAttribution? FactAttribution(JsonElement store, Func<IEnumerable<string?>, int[]> numbers,
        IReadOnlyDictionary<int, List<string>> inStatement, IReadOnlyDictionary<string, List<string>> thingsByIdentity)
    {
        if (!store.TryGetProperty("snapshot", out var snapshot) || snapshot.ValueKind != JsonValueKind.Object
            || !snapshot.TryGetProperty("Facts", out var facts) || !store.GetProperty("identity").TryGetProperty("bindings", out var bindings))
            return null;
        var statementsOf = facts.EnumerateArray().Where(f => f.TryGetProperty("FactId", out var id) && id.ValueKind == JsonValueKind.String)
            .ToDictionary(f => f.GetProperty("FactId").GetString()!,
                f => f.TryGetProperty("Sources", out var s) && s.ValueKind == JsonValueKind.Array ? numbers(s.EnumerateArray().Select(x => x.GetString())) : [],
                StringComparer.Ordinal);
        var identityOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in store.GetProperty("identity").GetProperty("decisions").EnumerateArray())
            if (d.TryGetProperty("DecisionId", out var decisionId) && decisionId.GetString() is { Length: > 0 } key)
                identityOf.TryAdd(key, d.GetProperty("Status").GetInt32() == 0 && d.TryGetProperty("ConfirmedEntityId", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()! : d.GetProperty("Observation").GetProperty("EntityId").GetString() ?? "");
        int total = 0, right = 0, wrong = 0, merged = 0, unscored = 0;
        foreach (var binding in bindings.EnumerateArray())
        {
            total++;
            var statements = statementsOf.GetValueOrDefault(binding.GetProperty("factId").GetString() ?? "") ?? [];
            var held = identityOf.TryGetValue(binding.GetProperty("decisionId").GetString() ?? "", out var identity)
                ? thingsByIdentity.GetValueOrDefault(identity) ?? [] : [];
            if (statements.Length == 0 || held.Count == 0) { unscored++; continue; }
            if (held.Count > 1) { merged++; continue; }
            if (statements.Any(n => inStatement.GetValueOrDefault(n)?.Contains(held[0]) == true)) right++; else wrong++;
        }
        return new(total, right, wrong, merged, unscored);
    }

    /// <summary>
    /// B-cubed and pairwise precision, recall and F1 (Neo4j's resolution metric is B-cubed), over the labelled mentions: each
    /// is an item, its identity the system's cluster and its real thing the key's. A wrong merge lowers precision, a split
    /// recall. Mentions the store never made are not items; the levels' "found" counts measure them.
    /// </summary>
    internal static IdentityKindsClustering Clustering(IReadOnlyList<LabelledMention> labelled)
    {
        if (labelled.Count == 0) return new(0, 0, 0, 0, 0, 0, 0);
        var byIdentity = labelled.GroupBy(l => l.Identity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var byThing = labelled.GroupBy(l => l.Thing!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var both = labelled.GroupBy(l => (l.Identity, l.Thing!)).ToDictionary(g => g.Key, g => g.Count());
        double precision = 0, recall = 0;
        foreach (var l in labelled)
        {
            var common = both[(l.Identity, l.Thing!)];
            precision += (double)common / byIdentity[l.Identity];
            recall += (double)common / byThing[l.Thing!];
        }
        precision /= labelled.Count; recall /= labelled.Count;
        static long Pairs(int n) => (long)n * (n - 1) / 2;
        var together = both.Values.Sum(Pairs);
        var sameIdentity = byIdentity.Values.Sum(Pairs);
        var sameThing = byThing.Values.Sum(Pairs);
        var pairPrecision = sameIdentity == 0 ? 1 : (double)together / sameIdentity;
        var pairRecall = sameThing == 0 ? 1 : (double)together / sameThing;
        static double F1(double p, double r) => p + r == 0 ? 0 : 2 * p * r / (p + r);
        return new(Math.Round(precision, 4), Math.Round(recall, 4), Math.Round(F1(precision, recall), 4),
            Math.Round(pairPrecision, 4), Math.Round(pairRecall, 4), Math.Round(F1(pairPrecision, pairRecall), 4), labelled.Count);
    }

    /// <summary>
    /// Each split thing's identities in the order the conversation first gave them; every one after the first is classified
    /// against the first by <see cref="SplitCause"/>, in that order of precedence (type, then name words, then its decision).
    /// </summary>
    private static (IReadOnlyDictionary<string, int> Causes, IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> ByKind) SplitCauses(
        IdentityKindsGold gold, IReadOnlyList<LabelledMention> labelled, IReadOnlyDictionary<string, List<string>> identitiesByThing,
        IReadOnlyDictionary<string, int> firstStatus, string view)
    {
        var causes = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var byKind = new SortedDictionary<string, SortedDictionary<string, int>>(StringComparer.Ordinal);
        foreach (var thing in gold.Things.Where(t => identitiesByThing[t.Id].Count > 1))
        {
            var order = labelled.Where(l => l.Thing == thing.Id).OrderBy(l => l.Statement)
                .GroupBy(l => l.Identity, StringComparer.Ordinal).Select(g => g.First()).ToList();
            var first = order[0];
            foreach (var extra in order.Skip(1))
            {
                var cause = Kind(extra.Type) != Kind(first.Type) ? SplitCause.TypeChanged
                    : !Words(extra.Words).Overlaps(Words(first.Words)) ? SplitCause.OtherName
                    : view != IdentityGoldScorer.DecisionsView ? SplitCause.SameName
                    : firstStatus.GetValueOrDefault(extra.Identity, -1) == 1 ? SplitCause.PendingRepeat
                    : SplitCause.NewRepeat;
                causes[cause] = causes.GetValueOrDefault(cause) + 1;
                var kind = byKind.TryGetValue(thing.Kind, out var k) ? k : byKind[thing.Kind] = new(StringComparer.Ordinal);
                kind[cause] = kind.GetValueOrDefault(cause) + 1;
            }
        }
        return (causes, byKind.ToDictionary(p => p.Key, p => (IReadOnlyDictionary<string, int>)p.Value, StringComparer.Ordinal));
    }

    private static (string? Thing, string How) Label(Observation o, IReadOnlyList<string> candidates,
        IReadOnlyDictionary<string, IdentityKindsGold.Thing> things, IReadOnlyDictionary<string, HashSet<string>> names)
    {
        if (candidates.Count == 0) return (null, "the key mentions nothing in this statement");
        var words = MentionWords(o.Words);
        var kind = Kind(o.Type);
        var named = candidates.Where(c => names[c].Overlaps(words)).ToList();
        var namedAndTyped = named.Where(c => things[c].Type == kind).ToList();
        if (namedAndTyped.Count == 1) return (namedAndTyped[0], "name and type");
        if (namedAndTyped.Count > 1) return (null, "two things of this name and type in the statement");
        // IHC-SCORER (the owner's call, 23 Nov): an event named after a thing ("Rebecca Thornton's wedding", where the key
        // names only the friend) is not that thing, nor a thing named after an event. The thing's bare name under the other
        // type still is: a model may type a project an event.
        if (named.Count == 1 && (kind == "EVENT") != (things[named[0]].Type == "EVENT") && words.Except(names[named[0]]).Any())
            return (null, "an event named after a thing, or a thing after an event");
        if (named.Count == 1) return (named[0], "name, another type");
        if (named.Count > 1) return (null, "two things of this name in the statement");
        // A proper name that names none of the statement's things is a thing the key does not hold (the city a venue is
        // in, the town a house is in), not the one thing of its type. Only a role noun ("the vet") is labelled by type.
        if (IsProperName(o.Words)) return (null, "a name the key does not hold in this statement");
        var typed = candidates.Where(c => things[c].Type == kind).ToList();
        if (typed.Count == 1) return (typed[0], "the one thing of this type in the statement");
        return (null, typed.Count == 0 ? "no thing of this name or type in the statement" : "two things of this type, neither named");
    }

    /// <summary>
    /// A thing's name words without a possessive's owner: "her mother's Kindle" is "kindle", so a mention of the mother is
    /// not taken for the Kindle. The owner is a thing of its own when the key holds it.
    /// </summary>
    internal static IEnumerable<string> NameWords(string name)
    {
        // The place a thing is in names that place, not the thing: "Café Tortoni in Buenos Aires" is "café tortoni".
        name = PlaceItIsIn.Replace(name, "");
        var possessive = Possessive.Match(name);
        return possessive.Success ? Words(name).Except(Words(possessive.Value), StringComparer.Ordinal) : Words(name);
    }

    /// <summary>
    /// A stored mention's words without a possessive's owner: "Clara's cat Luna" names the cat, not Clara (the second paid
    /// check of 23 Nov counted it as a split of Clara). A mention that is only a possessive keeps its words.
    /// </summary>
    internal static HashSet<string> MentionWords(string words)
    {
        // IKB-SCORER-PLACE-MENTION: a common-noun head with the place it is in names the head, not the place: "the mosque in
        // Córdoba" is the mosque (the fourth paid check, cover4-1). A head that holds a capitalised word keeps its place,
        // which tells namesakes apart ("Córdoba in Spain", "another Café Tortoni in Rosario").
        var place = PlaceItIsIn.Match(words);
        if (place.Success && !words[..place.Index].Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => char.IsUpper(w[0])))
            words = words[..place.Index];
        var all = Words(words);
        var possessive = Possessive.Match(words);
        if (!possessive.Success) return all;
        var owned = new HashSet<string>(all, StringComparer.Ordinal);
        owned.ExceptWith(Words(possessive.Value));
        return owned.Count > 0 ? owned : all;
    }

    // The owner is one word, with the word that points at it ("her mother's", "Clara's"): "Luna, Clara's cat" keeps "Luna"
    // (the third paid check, where a match from the start of the words took the cat's own name with its owner).
    // IHC-SCORER: an owner of several capitalised words, a title too ("Dr. Sosa's clinic", "Rebecca Thornton's wedding"),
    // is the owner as a whole: one word left "dr" and "rebecca" to name the owner's thing as the owner (the paid check of 24
    // Nov, ihc1-v1-glm-1 statement 49). A comma still ends it: "Luna, Clara's cat" keeps "Luna".
    private static readonly System.Text.RegularExpressions.Regex Possessive =
        new(@"(?:\b(?:her|his|my|our|their|your|the)\s+)?(?:\b\p{Lu}[\p{L}\p{M}]*\.?\s+){0,3}[\p{L}\p{M}]+['’]s(?=\s)",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex PlaceItIsIn =
        new(@"\s+in\s+\p{Lu}.*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Words that name something, as a writer stores a name: every word but the small ones starts with a capital or a
    /// digit ("Buenos Aires", "Q3"). "the vet" and "the lake house" are role nouns, which may be labelled by type.
    /// </summary>
    internal static bool IsProperName(string words)
    {
        var tokens = words.Split([' ', ',', '.', '(', ')', '\'', '’', '"'], StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !SmallWords.Contains(t.ToLowerInvariant()) && !(t.Length == 1 && char.IsLower(t[0]))).ToList();
        return tokens.Count > 0 && tokens.All(t => char.IsUpper(t[0]) || char.IsDigit(t[0]));
    }

    /// <summary>The words of a name: lower case, without accents or punctuation, without small words.</summary>
    internal static HashSet<string> Words(string text)
    {
        var plain = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            plain.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        // A possessive's apostrophe is punctuation, so "Inés's" is "ines" and a lone "s", which is dropped.
        return plain.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 1 && !SmallWords.Contains(w))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string Kind(string type)
    {
        var upper = type.ToUpperInvariant();
        return TypeKind.GetValueOrDefault(upper, upper);
    }
}

/// <summary>One kind's real things: how many, right, extra identities (splits), merged into another thing, never stored.</summary>
internal sealed record IdentityKindsTally(int Things, int Right, int Splits, int Merged, int Missed);

/// <summary>One level's mentions: how many, found in the store under their thing, and found under a right thing.</summary>
internal sealed record IdentityKindsLevel(int Mentions, int Found, int Right);

/// <summary>ER-KIT: B-cubed and pairwise precision, recall and F1 over the labelled mentions (how many: <see cref="Mentions"/>).</summary>
internal sealed record IdentityKindsClustering(
    double BCubedPrecision, double BCubedRecall, double BCubedF1, double PairwisePrecision, double PairwiseRecall, double PairwiseF1, int Mentions);

/// <summary>ER-KIT: one kind's real things the store holds, and how many of them carry the key's type in every mention.</summary>
internal sealed record IdentityKindsTyping(int Stored, int Typed);

/// <summary>ER-KIT: the store's fact bindings, each scored against the real things its fact's statements mention.</summary>
internal sealed record IdentityKindsFactAttribution(int Bindings, int Right, int Wrong, int ToAMergedIdentity, int Unscored);

/// <summary>One mention of an identity in one statement, and the real thing it was labelled with (or why not).</summary>
internal sealed record LabelledMention(string Identity, string Words, string Type, int Statement, string? Thing, string How);

/// <summary>What <see cref="IdentityKindsScorer"/> found in one store.</summary>
internal sealed record IdentityKindsScore(
    string View,
    int Things,
    int Right,
    int WrongMerges,
    IReadOnlyDictionary<string, IReadOnlyList<string>> WrongMergeDetail,
    int Splits,
    IReadOnlyDictionary<string, int> SplitThings,
    int Missed,
    IReadOnlyDictionary<string, IdentityKindsTally> ByKind,
    IReadOnlyDictionary<string, IdentityKindsLevel> ByLevel,
    int PendingDecisions,
    int LabelledMentions,
    int UnlabelledMentions,
    int IdentitiesWithoutAStatement,
    IReadOnlyList<LabelledMention> Mentions)
{
    /// <summary>ER-KIT: the field's clustering scores, beside ours.</summary>
    public IdentityKindsClustering Clustering { get; init; } = new(0, 0, 0, 0, 0, 0, 0);
    /// <summary>ER-KIT: per kind, the stored real things and how many carry the key's type.</summary>
    public IReadOnlyDictionary<string, IdentityKindsTyping> TypeByKind { get; init; } = new Dictionary<string, IdentityKindsTyping>();
    /// <summary>
    /// ER-KIT: identities none of whose mentions names a thing of the key, with their first words. Spurious entities (words
    /// that only look like names) are among them, and so are real things the key does not hold (the city a venue is in).
    /// </summary>
    public IReadOnlyDictionary<string, string> IdentitiesOutsideTheKey { get; init; } = new Dictionary<string, string>();
    /// <summary>ER-KIT: why each split's extra identities were not joined to the first (<see cref="IdentityKindsScorer.SplitCause"/>).</summary>
    public IReadOnlyDictionary<string, int> SplitCauses { get; init; } = new Dictionary<string, int>();
    /// <summary>ER-KIT: the same, per kind.</summary>
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, int>> SplitCausesByKind { get; init; } = new Dictionary<string, IReadOnlyDictionary<string, int>>();
    /// <summary>ER-KIT: facts bound to the right identity (decisions view only; null for the entities view).</summary>
    public IdentityKindsFactAttribution? FactAttribution { get; init; }
}
