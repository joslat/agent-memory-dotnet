using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Scenarios.PublicGold;

/// <summary>
/// The Identity Kinds Benchmark's tier 2 answer key (IKB-2): the real things a conversation talks about, the pairs that
/// share a name, and every mention with the statement it is in, how it is named (by name, a cue, a role ...) and how
/// hard it is (level 1 to 4). Frozen before any run (scenarios/identity-kinds/whos-who-v1.gold.json, written by
/// strategy/features/evidence-based-identity/validation/identity-kinds/build_whos_who.py); never edited to make a result
/// pass.
/// </summary>
internal sealed partial record IdentityKindsGold(
    string Name,
    int Version,
    string Frozen,
    IReadOnlyDictionary<string, string> Levels,
    IReadOnlyList<IdentityKindsGold.Thing> Things,
    IReadOnlyList<IdentityKindsGold.Namesake> Namesakes,
    IReadOnlyList<IdentityKindsGold.Mention> Mentions)
{
    /// <summary>One real thing: its kind (person, animal, venue ...), the type a right extraction gives it, what it is.</summary>
    public sealed record Thing(string Id, string Kind, string Type, string What, int Mentions);

    public sealed record Namesake(string A, string B, string What);

    /// <summary>One mention: statement is 1-based over the whole conversation; session and message are 1-based too.</summary>
    public sealed record Mention(int Statement, int Session, int Message, string Thing, string As, int Level);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IdentityKindsGold Load(string path) =>
        JsonSerializer.Deserialize<IdentityKindsGold>(File.ReadAllBytes(path), Json)
        ?? throw new InvalidDataException($"'{path}' is not an identity kinds answer key.");

    /// <summary>The answer key beside a scenario file: <c>&lt;name&gt;.gold.json</c> for <c>&lt;name&gt;.json</c>, if there is one.</summary>
    public static string? BesideScenario(string scenarioPath)
    {
        var gold = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(scenarioPath))!, Path.GetFileNameWithoutExtension(scenarioPath) + ".gold.json");
        return File.Exists(gold) ? gold : null;
    }

    /// <summary>
    /// Refuses a key that does not fit the scenario: every mention's statement must be that session's message, and every
    /// mention must name a known thing. A key scored against another conversation would label nothing right.
    /// </summary>
    public void CheckAgainst(Scenario scenario)
    {
        var turns = scenario.Turns().ToList();
        var things = Things.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var m in Mentions)
        {
            if (!things.Contains(m.Thing))
                throw new InvalidDataException($"{Name}: mention in statement {m.Statement} names the unknown thing {m.Thing}");
            if (m.Statement < 1 || m.Statement > turns.Count || m.Session < 1 || m.Session > scenario.Sessions.Count ||
                m.Message < 1 || m.Message > scenario.Sessions[m.Session - 1].Messages.Count ||
                turns[m.Statement - 1].Text != scenario.Sessions[m.Session - 1].Messages[m.Message - 1])
                throw new InvalidDataException($"{Name}: statement {m.Statement} is not message {m.Message} of session {m.Session} of {scenario.Name}");
        }
    }

    /// <summary>
    /// The names a thing goes by in its description: the words before the first comma or parenthesis ("Valentina Ríos,
    /// the designer at Brisa (also 'Vale')" gives "Valentina Ríos"), and every name in quotes ("Vale").
    /// </summary>
    public static IReadOnlyList<string> NamesOf(Thing thing)
    {
        var head = thing.What.Split([',', '('], 2)[0].Trim();
        return [head, .. Quoted().Matches(thing.What).Select(m => m.Groups[1].Value.Trim()).Where(n => n.Length > 0)];
    }

    // A quote opens after a space or a parenthesis, so a possessive ("Inés's dog") is not one.
    [GeneratedRegex(@"(?<=[\s(])'([^']+)'")]
    private static partial Regex Quoted();
}
