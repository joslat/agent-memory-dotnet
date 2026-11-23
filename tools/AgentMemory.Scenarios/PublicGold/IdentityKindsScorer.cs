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
            labels);
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
        var all = Words(words);
        var possessive = Possessive.Match(words);
        if (!possessive.Success) return all;
        var owned = new HashSet<string>(all, StringComparer.Ordinal);
        owned.ExceptWith(Words(possessive.Value));
        return owned.Count > 0 ? owned : all;
    }

    // The owner is one word, with the word that points at it ("her mother's", "Clara's"): "Luna, Clara's cat" keeps "Luna"
    // (the third paid check, where a match from the start of the words took the cat's own name with its owner).
    private static readonly System.Text.RegularExpressions.Regex Possessive =
        new(@"(?:\b(?:her|his|my|our|their|your|the)\s+)?[\p{L}\p{M}]+['’]s(?=\s)",
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
    IReadOnlyList<LabelledMention> Mentions);
