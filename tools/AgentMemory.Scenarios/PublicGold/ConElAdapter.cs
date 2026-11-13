using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Scenarios.PublicGold;

/// <summary>
/// ConEL-2 (Joko and Hasibi, 2022; annotations MIT) as an identity scenario and its answer key: Wizard of Wikipedia
/// dialogues in which people mention things, and the Wikipedia entity each mention names (also "my favorite singer" to
/// "Kid Rock"). Each dialogue becomes one session of the user's turns, said verbatim; each gold group is one annotated
/// entity. Links carry no type, so a group matches a stored entity of any type.
/// </summary>
/// <remarks>
/// The scenario format holds only what the user says, so the assistant's turns are not replayed. The slice file holds
/// dialogue ids, turn numbers and hashes of the utterances, never their text.
/// </remarks>
internal static partial class ConElAdapter
{
    public const string SliceVersion = "conel2-slice-v1";
    public const string Source = "ConEL-2 (Joko and Hasibi, 2022), https://github.com/informagi/conversational-entity-linking-2022";
    public const string Licence = "Annotations MIT (Informagi); dialogues from Wizard of Wikipedia under its own terms and not redistributed here.";
    private const string TestFile = "ConEL22_EL_Test.json";
    private static readonly DateOnly FirstDay = new(2026, 3, 2);

    public static ConElSlice Select(string datasetDirectory, int dialogues = 20)
    {
        var path = Path.Combine(datasetDirectory, TestFile);
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var chosen = new List<ConElDialogueRef>();
        foreach (var dialogue in document.RootElement.EnumerateArray())
        {
            if (chosen.Count == dialogues) break;
            var turns = dialogue.GetProperty("turns").EnumerateArray().ToList();
            if (!turns.Any(t => Array(t, "personal_entity_annotations").Any())) continue;
            chosen.Add(new ConElDialogueRef(Id(dialogue), turns
                .Where(t => t.GetProperty("speaker").GetString() == "USER" && !string.IsNullOrWhiteSpace(t.GetProperty("utterance").GetString()))
                .Select(t => new ConElTurnRef(t.GetProperty("turn_number").GetInt32(), Hash(t.GetProperty("utterance").GetString()!))).ToList()));
        }

        return new ConElSlice(SliceVersion, Source, Licence,
            $"The first {dialogues} dialogues of the ConEL-2 test split, in file order, that hold at least one personal-entity " +
            "annotation. Every user turn of each, in order; the assistant's turns are not replayed. One dialogue per session.",
            TestFile, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))), chosen);
    }

    public static (Scenario Scenario, IdentityGold Gold) Build(ConElSlice slice, string datasetDirectory, int? sessions = null)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(datasetDirectory, slice.File)));
        var byId = document.RootElement.EnumerateArray().ToDictionary(Id, d => d, StringComparer.Ordinal);
        var built = new List<Session>();
        var statementGroups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var keywords = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var statement = 0;
        foreach (var reference in sessions is { } limit ? slice.Dialogues.Take(limit) : slice.Dialogues)
        {
            if (!byId.TryGetValue(reference.DialogueId, out var dialogue))
                throw new InvalidDataException($"ConEL-2 dialogue {reference.DialogueId} is missing from {slice.File}.");
            var turns = dialogue.GetProperty("turns").EnumerateArray().ToDictionary(t => t.GetProperty("turn_number").GetInt32());
            var messages = new List<string>();
            foreach (var turnReference in reference.Turns)
            {
                var utterance = turns.TryGetValue(turnReference.Turn, out var turn) ? turn.GetProperty("utterance").GetString() : null;
                if (utterance is null || Hash(utterance) != turnReference.Sha256)
                    throw new InvalidDataException($"ConEL-2 dialogue {reference.DialogueId} turn {turnReference.Turn} does not match {slice.Version}: the dataset differs from the one the slice was frozen on.");
                messages.Add(utterance.Trim());
                statement++;
                var mentioned = new List<string>();
                void Link(string? entity, params string?[] words)
                {
                    if (string.IsNullOrWhiteSpace(entity)) return;
                    if (!keywords.TryGetValue(entity, out var set)) keywords[entity] = set = new(StringComparer.Ordinal);
                    foreach (var word in words.Append(Title(entity)))
                        if (word?.Trim().ToLowerInvariant() is { Length: >= 2 } keyword) set.Add(keyword);
                    if (!mentioned.Contains(entity, StringComparer.Ordinal)) mentioned.Add(entity);
                }

                foreach (var link in Array(turn, "el_annotations"))
                    Link(link.TryGetProperty("entity", out var e) ? e.GetString() : null, link.TryGetProperty("mention", out var m) ? m.GetString() : null);
                foreach (var link in Array(turn, "personal_entity_annotations"))
                    Link(link.TryGetProperty("entity", out var e) ? e.GetString() : null,
                        link.TryGetProperty("explicit_entity_mention", out var m) ? m.GetString() : null);
                statementGroups[statement.ToString(CultureInfo.InvariantCulture)] = mentioned;
            }

            built.Add(new Session(FirstDay.AddDays(built.Count), "a chat with the assistant", messages));
        }

        var scenario = EcbPlusAdapter.NewScenario(slice.Version, "ConEL-2 conversations (independent identity gold)", Source + "; " + Licence, built);
        var gold = new IdentityGold("conel2-gold-v1-derived-from-" + slice.Version, scenario.Sha256,
            keywords.ToDictionary(k => k.Key, k => new GoldGroup(IdentityGold.AnyKind, k.Value.ToList()), StringComparer.Ordinal),
            statementGroups, IdentityGold.WordPrefix);
        return (scenario, gold);
    }

    /// <summary>A Wikipedia title as words: "Beach_(geography)" reads "beach".</summary>
    private static string Title(string entity) => Parenthetical().Replace(entity.Replace('_', ' '), "").Trim();

    [GeneratedRegex(@"\s*\([^)]*\)\s*$")]
    private static partial Regex Parenthetical();

    private static IEnumerable<JsonElement> Array(JsonElement turn, string property) =>
        turn.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array ? value.EnumerateArray() : [];

    private static string Id(JsonElement dialogue) => dialogue.GetProperty("dialogue_id") is var id && id.ValueKind == JsonValueKind.Number
        ? id.GetRawText() : id.GetString() ?? "";

    private static string Hash(string utterance) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(utterance)));

    public static string SliceJson(ConElSlice slice) => JsonSerializer.Serialize(slice, IdentityGold.Json);

    public static ConElSlice LoadSlice(string path) =>
        JsonSerializer.Deserialize<ConElSlice>(File.ReadAllBytes(path), IdentityGold.Json)
        ?? throw new InvalidDataException($"'{path}' is not a ConEL-2 slice.");
}

/// <summary>A frozen ConEL-2 selection: dialogue ids, user-turn numbers and utterance hashes only.</summary>
internal sealed record ConElSlice(string Version, string Source, string Licence, string Rule, string File, string FileSha256, IReadOnlyList<ConElDialogueRef> Dialogues);

internal sealed record ConElDialogueRef(string DialogueId, IReadOnlyList<ConElTurnRef> Turns);

/// <summary>A user turn's number and the SHA-256 of its utterance.</summary>
internal sealed record ConElTurnRef(int Turn, string Sha256);
