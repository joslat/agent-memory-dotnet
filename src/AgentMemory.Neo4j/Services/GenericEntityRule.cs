using System.Text.RegularExpressions;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// Which generic entities a consolidation closes (<c>ConsolidationOptions.CloseGenericEntities</c>, AMDREAM001): Dreaming
/// round 2's P4s, as measured in the router arena (<c>dream_passes.p4</c>, <c>dream_passes2.p4s</c>). An entity is generic
/// when it has a type, no <c>|</c> in its name, at most four words, and is an <c>OBJECT</c> or a thing without a name (its
/// name starts lower-case and it is not a <c>PERSON</c>). A generic entity some live fact or preference of its owner names is
/// closed: the fact carries what was said. One no fact or preference names is spared: it may be the only carrier (Dreaming
/// round 1 closed "New school backpack (OBJECT)", which alone held "Vasco tried on his new school backpack").
/// </summary>
internal static class GenericEntityRule
{
    internal sealed record EntityRow(string Id, string? OwnerId, string Name, string Type);

    internal sealed record Decision(IReadOnlyList<EntityRow> Closed, IReadOnlyList<EntityRow> Spared);

    private static readonly Regex Space = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex NotWord = new(@"[^\w\s]", RegexOptions.Compiled);

    /// <summary>Generic by shape and kind; whether it is closed or spared depends on what names it.</summary>
    internal static bool IsGeneric(string name, string type)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrEmpty(name) || name.Contains('|')) return false;
        if (name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length > 4) return false;
        var types = type.ToUpperInvariant().Replace(',', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return types.Contains("OBJECT") || (char.IsLower(name[0]) && !types.Contains("PERSON"));
    }

    /// <summary>Lower-case, underscores as spaces, punctuation as spaces, single spaces.</summary>
    internal static string Normalize(string? text)
    {
        var t = Space.Replace((text ?? string.Empty).Replace('_', ' ').ToLowerInvariant(), " ").Trim();
        return string.Join(' ', NotWord.Replace(t, " ").Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Each generic entity closed when its owner's live facts and preferences name it, spared when they do not.</summary>
    internal static Decision Decide(IEnumerable<EntityRow> entities, IEnumerable<(string? OwnerId, string Text)> statements)
    {
        var said = statements
            .GroupBy(s => s.OwnerId ?? string.Empty)
            .ToDictionary(g => g.Key, g => " " + string.Join(' ', g.Select(s => Normalize(s.Text)).Where(t => t.Length > 0)) + " ");
        var closed = new List<EntityRow>();
        var spared = new List<EntityRow>();
        foreach (var e in entities)
        {
            if (!IsGeneric(e.Name, e.Type)) continue;
            var name = Normalize(e.Name);
            var text = said.GetValueOrDefault(e.OwnerId ?? string.Empty, " ");
            if (name.Length > 0 && text.Contains(" " + name + " ", StringComparison.Ordinal)) closed.Add(e);
            else spared.Add(e);
        }
        return new Decision(closed, spared);
    }
}
