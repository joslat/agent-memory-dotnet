using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.Validation.Routing;

/// <summary>
/// A routing test set (format <c>routing-set/1</c>, root PLAN 40.61): questions and turns, each with the memory kinds that
/// hold its answer. Frozen before the router: its sha256 is recorded, and the held-out split is read only once the router
/// is frozen.
/// </summary>
public sealed record RoutingSet
{
    /// <summary>The format this set is written in.</summary>
    public required string Format { get; init; }

    /// <summary>The kinds a router chooses between.</summary>
    public required IReadOnlyList<string> Kinds { get; init; }

    /// <summary>The items.</summary>
    public required IReadOnlyList<RoutingItem> Items { get; init; }

    /// <summary>The sha256 of the file it was read from, lower-case hex; null when not read from a file.</summary>
    public string? Sha256 { get; init; }

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads a set; refuses an item whose gold names a kind the set does not declare.</summary>
    public static RoutingSet Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var set = JsonSerializer.Deserialize<RoutingSet>(bytes, Json) ?? throw new JsonException("The routing set is empty.");
        if (set.Format != "routing-set/1") throw new JsonException($"format is '{set.Format}', this reader reads 'routing-set/1'");
        var kinds = new HashSet<string>(set.Kinds, StringComparer.Ordinal);
        foreach (var item in set.Items)
        {
            if (item.Needs.SelectMany(g => g).Concat(item.Acceptable).FirstOrDefault(k => !kinds.Contains(k)) is { } unknown)
                throw new JsonException($"item '{item.Id}' names kind '{unknown}', which the set does not declare");
            if (item.Needs.Any(g => g.Count == 0)) throw new JsonException($"item '{item.Id}' has an empty group");
        }
        return set with { Sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() };
    }
}

/// <summary>A question or turn, and the kinds that hold its answer.</summary>
public sealed record RoutingItem
{
    /// <summary>A stable id (<c>k1:…</c> for a labelled show turn, <c>pack:…</c> for a validation pack's question).</summary>
    public required string Id { get; init; }

    /// <summary><c>k1</c> or <c>pack</c>.</summary>
    public string Source { get; init; } = "";

    /// <summary>What was said or asked.</summary>
    public required string Text { get; init; }

    /// <summary>telling, correction, recall or task.</summary>
    public string Kind { get; init; } = "";

    /// <summary>
    /// The gold: groups of kinds; a group is satisfied when any of its kinds is read, and every group is needed. Empty when
    /// the turn needs no memory (a telling, a correction).
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Needs { get; init; } = [];

    /// <summary>Kinds that may hold the answer too: reading them is not waste.</summary>
    public IReadOnlyList<string> Acceptable { get; init; } = [];

    /// <summary><c>dev</c> or <c>heldout</c>.</summary>
    public required string Split { get; init; }

    /// <summary>multiPart, multiHop, needsAll, abstain, asOf.</summary>
    public IReadOnlyList<string> Flags { get; init; } = [];

    /// <summary>Why the second labelling pass changed this item's gold, or null.</summary>
    public string? SecondPass { get; init; }
}
