using System.Text.RegularExpressions;
using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 9 · Two lanes (the owner's idea): a cheap test of how clear the turn is; a clear one takes the fast lane, an unclear one
/// the precise lane. Clear: no turn before it, no other-language letters, at most 14 words, a question mark or plain small talk.
/// </summary>
public sealed partial class TwoLanes(IContestant fast, IContestant precise, string fastName, string preciseName) : IContestant
{
    public string Family => "lanes";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?> { ["fast"] = fastName, ["precise"] = preciseName };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var clear = IsClear(item);
        var decision = clear ? fast.Decide(item, context) : precise.Decide(item, context);
        return decision with { Lane = clear ? "fast" : "precise" };
    }

    [GeneratedRegex("[áéíóúñüöäßç¿¡]", RegexOptions.IgnoreCase)]
    private static partial Regex OtherLanguage();

    public static bool IsClear(MatrixItem item) =>
        item.Prior.Count == 0 && !OtherLanguage().IsMatch(item.Text)
        && item.Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length <= 14
        && (item.Text.Contains('?') || RulesV2.IsSmallTalk(item.Text));
}
