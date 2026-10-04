using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Jar;

/// <summary>A judge's answer for one turn: P(yes) for every memory it was shown ("door|memory"), and its time.</summary>
public sealed record JudgeAnswer(IReadOnlyDictionary<string, double> Yes, double Seconds);

/// <summary>
/// One labelled set as the jar reads it: the turns' labels (a frozen matrix), what every memory type found on each turn
/// (the generation's candidates file: <c>candidates-v4.json</c> at the generation's root, <c>candidates-fresh.json</c> in a
/// fresh set's folder) and the recorded answers of each judge (<c>jev-&lt;judge&gt;.json</c> beside the candidates), read
/// when first asked.
/// </summary>
public sealed class JarSet(string name, string directory, IReadOnlyList<MatrixItem> items, IReadOnlyDictionary<string, JsonElement> candidates)
{
    private readonly Dictionary<string, IReadOnlyDictionary<string, JudgeAnswer>?> _answers = [];

    public string Name { get; } = name;

    public string Directory { get; } = directory;

    public IReadOnlyList<MatrixItem> Items { get; } = items;

    public IReadOnlyDictionary<string, JsonElement> Candidates { get; } = candidates;

    /// <param name="atRoot">The training set recorded at the generation's root (its candidates file is named for v4's recording).</param>
    public static JarSet Read(string name, string directory, string matrix, bool atRoot)
    {
        var candidates = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            File.ReadAllBytes(Path.Combine(directory, atRoot ? "candidates-v4.json" : "candidates-fresh.json")))
            ?? throw new JsonException("the candidates file is empty");
        using var document = JsonDocument.Parse(File.ReadAllBytes(matrix));
        var items = document.RootElement.GetProperty("items").Deserialize<List<MatrixItem>>()
            ?? throw new JsonException("the matrix has no items");
        return new JarSet(name, directory, items, candidates);
    }

    /// <summary>A judge's answers, by turn id; null when this set has no answers from it.</summary>
    public IReadOnlyDictionary<string, JudgeAnswer>? Answers(string judge)
    {
        if (_answers.TryGetValue(judge, out var cached))
            return cached;
        var path = Path.Combine(Directory, $"jev-{judge}.json");
        IReadOnlyDictionary<string, JudgeAnswer>? read = null;
        if (File.Exists(path))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            var answers = new Dictionary<string, JudgeAnswer>();
            foreach (var turn in document.RootElement.GetProperty("answers").EnumerateObject())
            {
                var yes = turn.Value.GetProperty("yes").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble());
                var seconds = turn.Value.TryGetProperty("seconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
                answers[turn.Name] = new JudgeAnswer(yes, seconds);
            }
            read = answers;
        }
        return _answers[judge] = read;
    }
}

/// <summary>
/// One turn as the jar scores it: its labels, everything any memory type found, what today's recall kept, its fold, and
/// every memory found with its own search similarity (for the forms without a judge).
/// </summary>
public sealed class JarTurn
{
    public required JarSet Set { get; init; }

    public required string Id { get; init; }

    public required IReadOnlyList<IReadOnlyList<string>> Needs { get; init; }

    /// <summary>Needed or acceptable: letting these in is never clutter.</summary>
    public required IReadOnlySet<string> Useful { get; init; }

    /// <summary>Every needed group was found by some memory type, so some form can serve the turn.</summary>
    public required bool Servable { get; init; }

    /// <summary>The memories found that the turn neither needs nor accepts: what a form could let in as clutter.</summary>
    public required int ClutterFound { get; init; }

    public required IReadOnlySet<string> Today { get; init; }

    public required int Fold { get; init; }

    /// <summary>Every memory found with a similarity, best first (ties: by id, then type, both descending).</summary>
    public required IReadOnlyList<(double Score, string Memory, string Door)> Scored { get; init; }

    public JudgeAnswer? Answer(string judge) => Set.Answers(judge)?.GetValueOrDefault(Id);

    /// <summary>Whether every needed group is in <paramref name="admitted"/>.</summary>
    public bool ServedBy(IReadOnlySet<string> admitted) => Needs.All(group => group.Any(admitted.Contains));

    /// <summary>The working memory (the profile, the last messages: ids "W…") is always in and never scored.</summary>
    public static bool IsMemory(string id) => !id.StartsWith('W');

    /// <summary>The cross-validation fold: the turn id's sha256, as one unsigned number, mod 5.</summary>
    public static int FoldOf(string id) =>
        (int)(new BigInteger(SHA256.HashData(Encoding.UTF8.GetBytes(id)), isUnsigned: true, isBigEndian: true) % 5);

    public static IReadOnlyList<JarTurn> Of(params JarSet[] sets)
    {
        var turns = new List<JarTurn>();
        foreach (var set in sets)
        {
            foreach (var item in set.Items)
            {
                var c = set.Candidates[item.Id];
                var rows = Rows(c, "wide").Concat(Rows(c, "reading")).ToList();
                var found = rows.Select(r => r.Memory).ToHashSet();
                var needed = item.Needs.SelectMany(g => g).ToHashSet();
                var useful = needed.Union(item.Acceptable).ToHashSet();
                turns.Add(new JarTurn
                {
                    Set = set,
                    Id = item.Id,
                    Needs = item.Needs,
                    Useful = useful,
                    Servable = item.Needs.Count > 0 && item.Needs.All(g => g.Any(found.Contains)),
                    ClutterFound = found.Count(m => IsMemory(m) && !useful.Contains(m)),
                    Today = Rows(c, "today").Select(r => r.Memory).Where(IsMemory).ToHashSet(),
                    Fold = FoldOf(item.Id),
                    Scored = [.. rows.Where(r => r.Score is not null && IsMemory(r.Memory))
                        .Select(r => (Score: r.Score!.Value, r.Memory, r.Door))
                        .OrderByDescending(r => r.Score).ThenByDescending(r => r.Memory, StringComparer.Ordinal)
                        .ThenByDescending(r => r.Door, StringComparer.Ordinal)],
                });
            }
        }
        return turns;
    }

    // A candidates row: [memory, door, similarity or null, rank].
    private static IEnumerable<(string Memory, string Door, double? Score)> Rows(JsonElement turn, string name) =>
        turn.GetProperty(name).EnumerateArray().Select(r => (
            r[0].GetString()!, r[1].GetString()!, r[2].ValueKind == JsonValueKind.Number ? r[2].GetDouble() : (double?)null));
}
