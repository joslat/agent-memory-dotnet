using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Scenarios.PublicGold;

/// <summary>
/// An identity answer key: gold groups (one real-world thing each, with its kind and the words that name it) and, per
/// statement (1-based, in the order said), the groups that statement mentions. The JSON shape is the frozen Bariloche
/// gold's (strategy bariloche-identity-gold-v1.json) plus two optional fields for derived public gold:
/// <see cref="Match"/> and <see cref="StatementPartitions"/>.
/// </summary>
/// <param name="Version">The answer key's version.</param>
/// <param name="ScenarioSha256">The scenario it scores; a run of another scenario is not scored with it.</param>
/// <param name="Groups">Gold groups by id.</param>
/// <param name="StatementGroups">Statement number (1-based, as text) to the groups it mentions.</param>
/// <param name="Match">
/// <see cref="Substring"/> (hand-written keywords; the Python metric's rule) or <see cref="WordPrefix"/> (keywords derived
/// from source annotations: a keyword matches where a word starts with it, so "enter" never matches "Center").
/// </param>
/// <param name="StatementPartitions">
/// ECB+ only: statement number to "topic/subtopic". Two subtopics of one topic are two similar but different real events.
/// </param>
internal sealed record IdentityGold(
    string Version,
    string ScenarioSha256,
    IReadOnlyDictionary<string, GoldGroup> Groups,
    IReadOnlyDictionary<string, IReadOnlyList<string>> StatementGroups,
    string Match = IdentityGold.Substring,
    IReadOnlyDictionary<string, string>? StatementPartitions = null)
{
    public const string Substring = "substring";
    public const string WordPrefix = "word-prefix";

    /// <summary>A group of this kind matches a mention of any type (ConEL-2 links carry no type).</summary>
    public const string AnyKind = "ANY";

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static IdentityGold Load(string path) =>
        JsonSerializer.Deserialize<IdentityGold>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException($"'{path}' is not an identity gold file.");

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

/// <summary>One real-world thing: its kind (EVENT, PERSON, ORGANIZATION, LOCATION, or ANY) and the words that name it.</summary>
internal sealed record GoldGroup(string Kind, IReadOnlyList<string> Keywords);
