using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 5 · The gatekeeper (the owner's idea: route at the gate of each memory, after the search). Every searching door is
/// searched wide open; then each door's own gate admits its items at or above the door's floor, within <c>margin</c> of
/// the door's own best, at most the door's <c>top</c>; a door is open when its gate let something in. The reading doors
/// (now, then, due, faded) select by time, not similarity, so no similarity gate can judge them: they open on the turn's
/// cue words (rules v2's). 13 · With <c>abstain</c>: when no door's best item reaches it, nothing is admitted.
/// 10 · With <c>rewrite</c>: a follow-up is searched together with the turn before.
/// </summary>
public sealed class Gatekeeper(GateSettings settings) : IContestant
{
    public GateSettings Settings { get; } = settings;

    public string Family => "gates";

    public IReadOnlyDictionary<string, object?> Parameters => Settings.ToParameters();

    private static readonly int SearchingSearches = Doors.SearchesOf(Doors.Searching.ToHashSet());

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var reading = RulesV2.CuedReading(item).ToHashSet();
        var candidates = context.Source(Settings.Rewrite, wide: true);
        var searches = SearchingSearches + Doors.SearchesOf(reading.Where(d => d != Door.Temporal).ToHashSet());
        if (Settings.Abstain is { } abstain && candidates.Select(c => c.Score ?? 0).DefaultIfEmpty(0).Max() < abstain)
            return Decision.Nothing(searches) with { Wide = true, SearchedDoors = Doors.Searching.Concat(reading).ToHashSet() };
        if (reading.Contains(Door.Temporal) && context.Record.Doors?.GetValueOrDefault("temporal:wide") is { } current)
        {
            // The facts searched as currently valid: the semantic and derived gates judge those instead.
            var facts = Candidates.Of(context.Data.World, current).Where(c => c.Section == RecallSection.Facts);
            candidates = [.. candidates.Where(c => c.Section != RecallSection.Facts), .. facts];
        }
        var kept = new List<Candidate>();
        var open = new HashSet<Door>(reading);
        foreach (var door in Doors.Searching)
        {
            var mine = candidates.Where(c => c.Door == door && c.Score is not null).OrderByDescending(c => c.Score!.Value).ToList();
            if (mine.Count == 0) continue;
            var best = mine[0].Score!.Value;
            var floor = Settings.Floor.GetValueOrDefault(door, Settings.DefaultFloor);
            var chosen = mine.Where(c => c.Score >= floor && (Settings.Margin is null || c.Score >= best - Settings.Margin)).ToList();
            var top = Settings.Top.TryGetValue(door, out var t) ? t : Settings.DefaultTop;
            var admitted = top is > 0 ? chosen.Take(top.Value).ToList() : chosen;
            if (admitted.Count == 0) continue;
            kept.AddRange(admitted);
            open.Add(door);
        }
        foreach (var door in reading.Where(d => d != Door.Temporal)) kept.AddRange(context.Reading(door));
        return new Decision(kept.Select(c => c.Id).ToHashSet(StringComparer.Ordinal), open, searches)
        {
            Wide = true, SearchedDoors = Doors.Searching.Concat(reading).ToHashSet(),
        };
    }
}

/// <summary>A gatekeeper's settings: a floor, a top and a margin for every searching door (or one for all), abstain, rewrite.</summary>
public sealed record GateSettings
{
    public double DefaultFloor { get; init; }
    public int? DefaultTop { get; init; }
    public double? Margin { get; init; }
    public IReadOnlyDictionary<Door, double> Floor { get; init; } = new Dictionary<Door, double>();

    /// <summary>A door's own top; a null value means no top for that door (overriding the default).</summary>
    public IReadOnlyDictionary<Door, int?> Top { get; init; } = new Dictionary<Door, int?>();
    public double? Abstain { get; init; }
    public bool Rewrite { get; init; }

    public GateSettings WithFloor(Door door, double value) => this with { Floor = new Dictionary<Door, double>(Floor) { [door] = value } };

    public GateSettings WithTop(Door door, int? value) => this with { Top = new Dictionary<Door, int?>(Top) { [door] = value } };

    public IReadOnlyDictionary<string, object?> ToParameters()
    {
        var p = new Dictionary<string, object?> { ["floor"] = DefaultFloor, ["margin"] = Margin, ["top"] = DefaultTop, ["reading"] = "cues (rules v2)" };
        foreach (var (door, value) in Floor) p[$"floor_{Doors.Name(door)}"] = value;
        foreach (var (door, value) in Top) p[$"top_{Doors.Name(door)}"] = value;
        if (Abstain is not null) p["abstain"] = Abstain;
        if (Rewrite) p["rewrite"] = true;
        return p;
    }
}
