using System.Text.Json.Serialization;

namespace AgentMemory.RouterArena.Jar;

/// <summary>
/// One way of deciding which memories reach the prompt after every memory type has searched, with its settings: the unit
/// the jar chooses by cross-validation, freezes and reads once (<c>final-forms.json</c>). Families:
/// <list type="bullet">
/// <item><c>today</c>: the shipped recall, as recorded.</item>
/// <item><c>gate</c>: a memory is let in when its <see cref="Judge"/>'s probability reaches <see cref="T"/>.</item>
/// <item><c>blend</c>: one score from two judges, <see cref="W"/>·online + (1 − <see cref="W"/>)·local, in at <see cref="T"/>.</item>
/// <item><c>hybrid</c>: the local judge decides each memory type it is sure about (all below <see cref="Low"/> or above
/// <see cref="High"/>), the online judge the rest at <see cref="T"/>.</item>
/// <item><c>module</c>: each memory type says yes for itself as a whole (its best memory at <see cref="T"/>) and then
/// hands over everything it found.</item>
/// <item><c>cospool</c>, <c>cosfloor</c>, <c>cosdoor</c>: no judge; every memory found ranked by its search similarity
/// and cut at the best <see cref="K"/>, at the floor <see cref="F"/>, or at the best <see cref="K"/> per type.</item>
/// </list>
/// </summary>
public sealed record JarForm
{
    [JsonPropertyName("family")] public required string Family { get; init; }
    [JsonPropertyName("judge")] public string? Judge { get; init; }
    [JsonPropertyName("local")] public string? Local { get; init; }
    [JsonPropertyName("online")] public string? Online { get; init; }
    [JsonPropertyName("t")] public double T { get; init; }
    [JsonPropertyName("w")] public double W { get; init; }
    [JsonPropertyName("low")] public double Low { get; init; }
    [JsonPropertyName("high")] public double High { get; init; }
    [JsonPropertyName("k")] public int K { get; init; }
    [JsonPropertyName("f")] public double F { get; init; }

    public static readonly JarForm Today = new() { Family = "today" };

    /// <summary>What this form lets in on a turn, and the seconds it waits for its judges.</summary>
    /// <remarks>A judge with no answer for the turn fails open to today's recall.</remarks>
    public (IReadOnlySet<string> Admitted, double Seconds) Admit(JarTurn turn)
    {
        switch (Family)
        {
            case "today":
                return (turn.Today, 0);
            case "gate":
            {
                if (turn.Answer(Judge!) is not { } answer)
                    return (turn.Today, 0);
                return (answer.Yes.Where(y => y.Value >= T).Select(y => MemoryOf(y.Key)).Where(JarTurn.IsMemory).ToHashSet(), answer.Seconds);
            }
            case "blend":
            {
                if (turn.Answer(Online!) is not { } online || turn.Answer(Local!) is not { } local)
                    return (turn.Today, 0);
                var admitted = new HashSet<string>();
                foreach (var key in online.Yes.Keys.Union(local.Yes.Keys))
                {
                    var score = W * online.Yes.GetValueOrDefault(key) + (1 - W) * local.Yes.GetValueOrDefault(key);
                    if (score >= T && MemoryOf(key) is var memory && JarTurn.IsMemory(memory))
                        admitted.Add(memory);
                }
                return (admitted, Math.Max(online.Seconds, local.Seconds));
            }
            case "hybrid":
            {
                if (turn.Answer(Local!) is not { } local)
                    return (turn.Today, 0);
                var online = turn.Answer(Online!);
                var kept = new HashSet<string>();
                var asked = 0;
                foreach (var door in local.Yes.GroupBy(y => DoorOf(y.Key), y => (Memory: MemoryOf(y.Key), P: y.Value)))
                {
                    if (door.All(m => m.P < Low || m.P > High))
                    {
                        kept.UnionWith(door.Where(m => m.P > High).Select(m => m.Memory));
                        continue;
                    }
                    asked++;
                    if (online is null)
                        kept.UnionWith(door.Where(m => m.P >= (Low + High) / 2).Select(m => m.Memory));
                    else
                        kept.UnionWith(online.Yes.Where(y => y.Key.StartsWith(door.Key + "|", StringComparison.Ordinal) && y.Value >= T).Select(y => MemoryOf(y.Key)));
                }
                return (kept.Where(JarTurn.IsMemory).ToHashSet(), local.Seconds + (asked > 0 ? online?.Seconds ?? 0 : 0));
            }
            case "module":
            {
                if (turn.Answer(Judge!) is not { } answer)
                    return (turn.Today, 0);
                var admitted = answer.Yes.GroupBy(y => DoorOf(y.Key))
                    .Where(door => door.Max(y => y.Value) >= T)
                    .SelectMany(door => door.Select(y => MemoryOf(y.Key)))
                    .Where(JarTurn.IsMemory);
                return (admitted.ToHashSet(), answer.Seconds);
            }
            case "cospool":
            {
                var got = new List<string>();
                foreach (var (_, memory, _) in turn.Scored)
                {
                    if (!got.Contains(memory))
                        got.Add(memory);
                    if (got.Count >= K)
                        break;
                }
                return (got.ToHashSet(), 0);
            }
            case "cosfloor":
                return (turn.Scored.Where(s => s.Score >= F).Select(s => s.Memory).ToHashSet(), 0);
            case "cosdoor":
            {
                var perDoor = new Dictionary<string, int>();
                var got = new HashSet<string>();
                foreach (var (_, memory, door) in turn.Scored)
                {
                    if (perDoor.GetValueOrDefault(door) < K)
                    {
                        perDoor[door] = perDoor.GetValueOrDefault(door) + 1;
                        got.Add(memory);
                    }
                }
                return (got, 0);
            }
            default:
                throw new InvalidOperationException($"unknown family '{Family}'");
        }
    }

    /// <summary>Whether this form waits on a judge that runs on this machine (its time counts when it reports none).</summary>
    public bool IsLocal => Family == "hybrid" || (Judge ?? "").StartsWith("xenc", StringComparison.Ordinal)
        || (Judge ?? "").StartsWith("laya", StringComparison.Ordinal) || (Judge ?? "").StartsWith("rerank", StringComparison.Ordinal);

    // An answer's key is "door|memory".
    private static string DoorOf(string key) => key[..key.IndexOf('|', StringComparison.Ordinal)];

    private static string MemoryOf(string key) => key[(key.IndexOf('|', StringComparison.Ordinal) + 1)..];
}
