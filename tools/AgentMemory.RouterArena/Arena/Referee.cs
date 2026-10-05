using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Arena;

/// <summary>One turn as the referee saw it.</summary>
public sealed record TurnScore(
    string Id, string Category, string Difficulty, IReadOnlyList<string> Edges, bool NeedsMemory, bool Served,
    IReadOnlyList<string> MissedDoors, IReadOnlyList<IReadOnlyList<string>> Missed, int RoutingMisses, int RetrievalMisses,
    int Noise, int Size, int Searches, int Model, string Lane, IReadOnlyList<string> Breach)
{
    /// <summary>The model time of the decision, in seconds.</summary>
    public double ModelSeconds { get; init; }

    /// <summary>The doors the turn needs (its labels; on a matrix without door labels, the doors holding what it needs).</summary>
    public IReadOnlyList<string> DoorsNeeded { get; init; } = [];

    /// <summary>The doors the contestant opened.</summary>
    public IReadOnlyList<string> DoorsOpened { get; init; } = [];

    /// <summary>Needed doors left shut.</summary>
    public int DoorsShut { get; init; }

    /// <summary>Opened doors the turn neither needs nor accepts.</summary>
    public IReadOnlyList<string> DoorsWasted { get; init; } = [];

    /// <summary>Every needed door open and none wasted.</summary>
    public bool DoorsExact { get; init; }
}

/// <summary>A contestant's score on a split.</summary>
public sealed record ContestantScore
{
    public required string Family { get; init; }
    public required IReadOnlyDictionary<string, object?> Params { get; init; }
    public int Turns { get; init; }
    public int Needing { get; init; }
    public int Served { get; init; }
    public double ServedShare { get; init; }
    public int MissedGroups { get; init; }
    public int RoutingMisses { get; init; }
    public int RetrievalMisses { get; init; }
    public int ServedIfRetrievalPerfect { get; init; }

    /// <summary>Whether the doors were scored against labels (matrix v2) or against the doors holding what is needed (v1).</summary>
    public bool DoorsLabelled { get; init; }

    /// <summary>Needed doors opened, over needed doors.</summary>
    public double DoorRecall { get; init; }

    /// <summary>Opened doors needed or accepted, over opened doors.</summary>
    public double DoorPrecision { get; init; }

    /// <summary>Turns with every needed door open and none wasted.</summary>
    public int DoorsExact { get; init; }

    public double NoisePerTurn { get; init; }
    public double SizePerTurn { get; init; }
    public double DoorsPerTurn { get; init; }
    public double SearchesPerTurn { get; init; }
    public double ModelCallsPerTurn { get; init; }
    public double ModelSecondsPerTurn { get; init; }
    public int Quiet { get; init; }
    public int NothingNeeded { get; init; }
    public int Breaches { get; init; }
    public IReadOnlyDictionary<string, int[]> ByCategory { get; init; } = new Dictionary<string, int[]>();

    /// <summary>Per door: [needed and opened, needed, opened and not needed or accepted].</summary>
    public IReadOnlyDictionary<string, int[]> ByDoor { get; init; } = new Dictionary<string, int[]>();

    public IReadOnlyDictionary<string, int> MissedByDoor { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, int> Lanes { get; init; } = new Dictionary<string, int>();
    public IReadOnlyList<TurnScore> Rows { get; init; } = [];
}

/// <summary>
/// The referee: scores the routing, never an answer. Per turn: served (every needed group reached the prompt), each miss
/// as the router's (the wide search or a reading door found it, the router kept it out) or the search's (never found: no
/// router can fix it), noise (ids no label needs or accepts), size, the doors needed against the doors opened, searches,
/// model calls, isolation breaches. The working door's recent messages never serve a need: a router must not rely on a
/// short conversation.
/// </summary>
public static class Referee
{
    /// <summary>Measured: median per call, GLM-5.3-Flash on Bitdeer at low reasoning effort (a decision that measured its own time says so).</summary>
    public const double ModelSeconds = 5.1;

    public static ContestantScore Score(IReadOnlyList<MatrixItem> items, ArenaData data, IContestant contestant,
        IDictionary<string, TurnContext> contexts)
    {
        var labelled = data.Matrix.DoorsLabelled;
        var rows = new List<TurnScore>();
        foreach (var item in items)
        {
            if (!contexts.TryGetValue(item.Id, out var context))
                contexts[item.Id] = context = new TurnContext(data.Records[item.Id], data);
            var decision = contestant.Decide(item, context);
            var admitted = decision.Admitted;
            var useful = item.Needs.SelectMany(g => g).Concat(item.Acceptable).ToHashSet(StringComparer.Ordinal);
            var missed = item.Needs.Where(g => !g.Any(admitted.Contains)).ToList();
            var routing = missed.Count(g => g.Any(Found(context).Contains));
            var needed = DoorsNeeded(item, data, labelled);
            var accepted = labelled ? item.DoorsAcceptable : item.Acceptable.Select(data.World.DoorOf).ToHashSet();
            var opened = decision.Lane == "oracle" ? needed : decision.Opened;
            rows.Add(new TurnScore(
                item.Id, item.Category, item.Difficulty, item.Edges, item.Needs.Count > 0, missed.Count == 0,
                [.. missed.SelectMany(g => g).Select(m => Doors.Name(data.World.DoorOf(m))).Distinct().Order(StringComparer.Ordinal)],
                missed, routing, missed.Count - routing,
                admitted.Count(m => !useful.Contains(m)), admitted.Count, decision.Searches, decision.ModelCalls, decision.Lane,
                [.. admitted.Where(m => m.StartsWith('X')).Order(StringComparer.Ordinal)])
            {
                ModelSeconds = decision.ModelSeconds ?? decision.ModelCalls * ModelSeconds,
                DoorsNeeded = [.. needed.Order().Select(Doors.Name)],
                DoorsOpened = [.. opened.Order().Select(Doors.Name)],
                DoorsShut = needed.Count(d => !opened.Contains(d)),
                DoorsWasted = [.. opened.Where(d => !needed.Contains(d) && !accepted.Contains(d)).Order().Select(Doors.Name)],
                DoorsExact = needed.All(opened.Contains) && opened.All(d => needed.Contains(d) || accepted.Contains(d)),
            });
        }
        var needing = rows.Where(r => r.NeedsMemory).ToList();
        var nothing = rows.Where(r => !r.NeedsMemory).ToList();
        double n = Math.Max(1, rows.Count);
        var doorsNeeded = rows.Sum(r => r.DoorsNeeded.Count);
        var doorsOpened = rows.Sum(r => r.DoorsOpened.Count);
        return new ContestantScore
        {
            Family = contestant.Family,
            Params = contestant.Parameters,
            Turns = rows.Count,
            Needing = needing.Count,
            Served = needing.Count(r => r.Served),
            ServedShare = Math.Round((double)needing.Count(r => r.Served) / Math.Max(1, needing.Count), 4),
            MissedGroups = rows.Sum(r => r.Missed.Count),
            RoutingMisses = rows.Sum(r => r.RoutingMisses),
            RetrievalMisses = rows.Sum(r => r.RetrievalMisses),
            ServedIfRetrievalPerfect = needing.Count(r => r.RoutingMisses == 0),
            DoorsLabelled = labelled,
            DoorRecall = Math.Round((double)(doorsNeeded - rows.Sum(r => r.DoorsShut)) / Math.Max(1, doorsNeeded), 3),
            DoorPrecision = Math.Round((double)(doorsOpened - rows.Sum(r => r.DoorsWasted.Count)) / Math.Max(1, doorsOpened), 3),
            DoorsExact = rows.Count(r => r.DoorsExact),
            NoisePerTurn = Math.Round(rows.Sum(r => r.Noise) / n, 2),
            SizePerTurn = Math.Round(rows.Sum(r => r.Size) / n, 2),
            DoorsPerTurn = Math.Round(doorsOpened / n, 2),
            SearchesPerTurn = Math.Round(rows.Sum(r => r.Searches) / n, 2),
            ModelCallsPerTurn = Math.Round(rows.Sum(r => r.Model) / n, 3),
            ModelSecondsPerTurn = Math.Round(rows.Sum(r => r.ModelSeconds) / n, 2),
            Quiet = nothing.Count(r => r.Size == 0),
            NothingNeeded = nothing.Count,
            Breaches = rows.Count(r => r.Breach.Count > 0),
            ByCategory = needing.GroupBy(r => r.Category).OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => new[] { g.Count(r => r.Served), g.Count() }),
            ByDoor = Doors.All.Select(Doors.Name).ToDictionary(name => name, name =>
            {
                return new[]
                {
                    rows.Count(r => r.DoorsNeeded.Contains(name) && r.DoorsOpened.Contains(name)),
                    rows.Count(r => r.DoorsNeeded.Contains(name)),
                    rows.Count(r => r.DoorsWasted.Contains(name)),
                };
            }),
            MissedByDoor = rows.SelectMany(r => r.MissedDoors).GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count()),
            Lanes = rows.GroupBy(r => r.Lane).ToDictionary(g => g.Key, g => g.Count()),
            Rows = rows,
        };
    }

    /// <summary>Everything any door could have let in for the turn: the wide search (of the turn and rewritten) and every reading door.</summary>
    private static HashSet<string> Found(TurnContext context) =>
        context.Source(false, wide: true).Concat(context.Source(true, wide: true))
            .Concat(Doors.Reading.SelectMany(context.Reading))
            .Select(c => c.Id).ToHashSet(StringComparer.Ordinal);

    /// <summary>The doors a turn needs: its labels, or (a matrix without door labels) the doors holding what it needs.</summary>
    public static IReadOnlySet<Door> DoorsNeeded(MatrixItem item, ArenaData data, bool labelled) =>
        labelled ? item.Doors : item.Needs.SelectMany(g => g).Select(data.World.DoorOf).ToHashSet();
}
