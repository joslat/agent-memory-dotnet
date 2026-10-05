using System.Globalization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Gate;

/// <summary>
/// What the judge is asked, word for word as it was measured: per memory type, the turn, the conversation before it,
/// today's date, what the type means, every memory found with its dates, and examples of what similar turns needed;
/// then one yes/no question per memory.
/// </summary>
internal static class GatePrompt
{
    /// <summary>What a turn needs each memory type for: the one wording the labels and every judge read.</summary>
    public static readonly IReadOnlyDictionary<string, string> Meanings = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["episodic"] = "something said earlier in a conversation (this one or a past one), by the user or the assistant",
        ["semantic"] = "a stated fact about the user or anyone or anything they know (knowledge taught to every user included)",
        ["entity-graph"] = "a person, place, organisation or pet in the user's life, or how two of them are connected",
        ["preference"] = "a taste, dislike, habit or diet of the user, or how they want answers",
        ["procedural"] = "how something is done, step by step: a procedure that worked before",
        ["reasoning"] = "how a similar task went before: what was tried, what happened, the outcome",
        ["prospective"] = "a reminder or deadline that is due or about to expire: what the user should not forget now",
    };

    public static readonly YesNo Question = new(
        "Should memory {0} be put in front of the assistant before it replies to the user's turn?",
        "Yes: it makes the reply right, personal or complete.",
        "No: the reply does not need it; it would be noise.");

    /// <summary>One example: a labelled turn, whether it needed this type, and (up to four of) the memories it needed.</summary>
    public sealed record Example(string Turn, bool NeededThisType, IReadOnlyList<string> MemoriesItNeeded);

    /// <summary>The state for one memory type's call; its memories keyed m1, m2, …</summary>
    public static object State(MemoryGateRequest request, string memoryType, IReadOnlyList<(string Key, string Text)> memories,
        IReadOnlyList<Example>? examples) => new
        {
            today = Day(request.Now),
            conversation = request.Conversation.Select(t => new { role = t.Role, text = t.Text }).ToList(),
            turn = request.Turn,
            memoryType,
            meaning = Meanings.TryGetValue(memoryType, out var meaning) ? meaning : memoryType,
            memories = memories.ToDictionary(m => m.Key, m => m.Text),
            examples = examples is { Count: > 0 } ? examples.Select(e => new { turn = e.Turn, neededThisType = e.NeededThisType, memoriesItNeeded = e.MemoriesItNeeded }).ToList() : null,
        };

    public static IReadOnlyDictionary<string, YesNo> Questions(IEnumerable<string> keys) =>
        keys.ToDictionary(k => k, k => Question with { Instructions = string.Format(CultureInfo.InvariantCulture, Question.Instructions, k) }, StringComparer.Ordinal);

    public static string Day(DateTimeOffset at) => at.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture);

    private static string Short(DateTimeOffset at) => at.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>A fact as the judge reads it: subject | predicate | object, then when it holds.</summary>
    public static string Describe(Fact fact, string? note = null)
    {
        var parts = new List<string>();
        if (fact.OccurredOn is { } on) parts.Add($"on {Short(on)}");
        else if (fact.ValidFrom is { } from)
            parts.Add(fact.ValidUntil is { } until ? $"from {Short(from)} until {Short(until)}" : $"since {Short(from)}");
        if (note is not null) parts.Add(note);
        var text = $"{fact.Subject} | {fact.Predicate} | {fact.Object}";
        return parts.Count == 0 ? text : $"{text} ({string.Join("; ", parts)})";
    }
}
