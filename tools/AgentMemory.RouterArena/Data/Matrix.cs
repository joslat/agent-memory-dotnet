using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.RouterArena.Data;

/// <summary>An earlier turn of the conversation a matrix turn follows.</summary>
public sealed record PriorTurn(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("text")] string Text);

/// <summary>What a turn should store (labelled; scored when the store side fights).</summary>
public sealed record StoreLabel(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("replaces")] string? Replaces);

/// <summary>
/// One turn of a routing matrix (<c>routing-matrix/1</c>): what the person says next, and its labels by memory id. Every
/// group in <see cref="Needs"/> is needed and any id in a group serves it; <see cref="Acceptable"/> ids help and are never
/// noise.
/// </summary>
public sealed record MatrixItem
{
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("category")] public string Category { get; init; } = "";
    [JsonPropertyName("edges")] public IReadOnlyList<string> Edges { get; init; } = [];
    [JsonPropertyName("lang")] public string Lang { get; init; } = "en";
    [JsonPropertyName("prior")] public IReadOnlyList<PriorTurn> Prior { get; init; } = [];
    [JsonPropertyName("text")] public required string Text { get; init; }
    [JsonPropertyName("needs")] public IReadOnlyList<IReadOnlyList<string>> Needs { get; init; } = [];
    [JsonPropertyName("acceptable")] public IReadOnlyList<string> Acceptable { get; init; } = [];
    [JsonPropertyName("stores")] public IReadOnlyList<StoreLabel> Stores { get; init; } = [];
    [JsonPropertyName("difficulty")] public string Difficulty { get; init; } = "";
    [JsonPropertyName("split")] public string? Split { get; init; }

    /// <summary>Version 2: the doors (memory types) the reply needs, any number of them.</summary>
    [JsonPropertyName("doors")] public IReadOnlyList<string> DoorNames { get; init; } = [];

    /// <summary>Version 2: doors that would help but are not needed.</summary>
    [JsonPropertyName("doorsAcceptable")] public IReadOnlyList<string> DoorsAcceptableNames { get; init; } = [];

    [JsonIgnore] public IReadOnlySet<Door> Doors => DoorNames.Select(Data.Doors.Parse).ToHashSet();

    [JsonIgnore] public IReadOnlySet<Door> DoorsAcceptable => DoorsAcceptableNames.Select(Data.Doors.Parse).ToHashSet();
}

/// <summary>A routing matrix: its items, the file's sha256 (a run names the set it was scored on), and the split rule.</summary>
public sealed record Matrix(IReadOnlyList<MatrixItem> Items, string Sha256, string Path)
{
    /// <summary>The held-out share, by the first byte of the id's sha256: the rule the frozen set was split with.</summary>
    public const double HeldOutShare = 0.35;

    public static Matrix Read(string path)
    {
        var bytes = File.ReadAllBytes(path);
        using var document = JsonDocument.Parse(bytes);
        var items = document.RootElement.GetProperty("items").Deserialize<List<MatrixItem>>()
            ?? throw new JsonException("the matrix has no items");
        return new Matrix(items, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), System.IO.Path.GetFullPath(path))
        {
            DoorsLabelled = items.Any(i => i.DoorNames.Count > 0),
        };
    }

    /// <summary>The item's split: as frozen in the file, or by the hash rule when the file carries none.</summary>
    public static string SplitOf(MatrixItem item) =>
        item.Split ?? (SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(item.Id))[0] < (int)(256 * HeldOutShare) ? "heldout" : "dev");

    /// <summary>Version 2: the turns carry the doors they need (a turn needing none has an empty list).</summary>
    public bool DoorsLabelled { get; init; }

    public IEnumerable<MatrixItem> InSplit(string split) =>
        Items.Where(i => split == "all" || SplitOf(i) == split);
}
