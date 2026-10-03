namespace AgentMemory.RouterArena.Data;

/// <summary>
/// The doors a router opens (root PLAN 40.72): eleven memory zones, and the forgetting switch. A turn may need any number
/// of them. Nine memory types, eleven zones: entity-graph, preference and derived are pieces of semantic memory stored and
/// searched separately; forgetting is a process across every type, not a zone.
/// </summary>
public enum Door
{
    Working, Episodic, Semantic, EntityGraph, Preference, Procedural, Reasoning, Temporal, BiTemporal, Prospective, Derived, Forgetting,
}

public static class Doors
{
    public static readonly Door[] All = Enum.GetValues<Door>();

    /// <summary>The door's name as the matrix labels it ("entity-graph", "bi-temporal").</summary>
    public static string Name(Door door) => door switch
    {
        Door.EntityGraph => "entity-graph",
        Door.BiTemporal => "bi-temporal",
        _ => door.ToString().ToLowerInvariant(),
    };

    /// <summary>
    /// What a turn needs the door for, as the matrix's labelling rule defines it (<c>routing-matrix-spec.md</c>, version
    /// 2): the one wording every asker (the model lane, JEV) reads, so a router is asked what the labels mean.
    /// </summary>
    public static string Meaning(Door door) => door switch
    {
        Door.Working => "who the user is in broad strokes (the profile: name, home, work, family at a glance) or the last few turns of this conversation",
        Door.Episodic => "something said earlier in a conversation (this one or a past one), by the user or the assistant",
        Door.Semantic => "a stated fact about the user or anyone or anything they know (knowledge taught to every user included)",
        Door.EntityGraph => "a person, place, organisation or pet in the user's life, or how two of them are connected",
        Door.Preference => "a taste, dislike, habit or diet of the user, or how they want answers",
        Door.Procedural => "how something is done, step by step: a procedure that worked before",
        Door.Reasoning => "how a similar task went before: what was tried, what happened, the outcome",
        Door.Temporal => "what is true now where an older value also exists (home, job, a time, a plan), or a date in the turn to resolve",
        Door.BiTemporal => "what was true at an earlier time, what changed, or what was believed before a correction",
        Door.Prospective => "a reminder or deadline that is due or about to expire: what the user should not forget now",
        Door.Derived => "a count, a full list or the latest of something, which nobody said but memory can compute from what was said",
        Door.Forgetting => "saying that something the user once told has faded from memory (rather than that it was never known)",
        _ => throw new ArgumentOutOfRangeException(nameof(door)),
    };

    public static Door Parse(string name)
    {
        var wanted = name.Trim().ToLowerInvariant().Replace('_', '-');
        foreach (var door in All)
            if (Name(door) == wanted || Name(door).Replace("-", "") == wanted) return door;
        throw new FormatException($"'{name}' is not a door");
    }

    /// <summary>The doors searched by similarity: each reads its own memories against the turn.</summary>
    public static readonly IReadOnlyList<Door> Searching =
        [Door.Episodic, Door.Semantic, Door.Derived, Door.EntityGraph, Door.Preference, Door.Procedural, Door.Reasoning];

    /// <summary>The doors that read the facts another way: as valid now, as of a date, by what is due, by what has faded.</summary>
    public static readonly IReadOnlyList<Door> Reading = [Door.Temporal, Door.BiTemporal, Door.Prospective, Door.Forgetting];

    /// <summary>
    /// The doors the library's shipped recall (the conversational preset) reads on every turn: the profile and the last
    /// messages, and every searching door (derived facts ride in the facts, traces of both kinds in one search).
    /// </summary>
    public static readonly IReadOnlySet<Door> Shipped = new HashSet<Door>([Door.Working, .. Searching]);

    /// <summary>
    /// The searches a set of open doors costs, as the library runs them: the working door none (the profile and the last
    /// messages are loaded on every turn); semantic, derived and temporal one facts search between them (derived facts
    /// ride in it; temporal runs it as currently valid); procedural and reasoning one traces search; every other door one.
    /// </summary>
    public static int SearchesOf(IReadOnlySet<Door> open) => open.Where(d => d != Door.Working).Select(d => d switch
    {
        Door.Semantic or Door.Derived or Door.Temporal => "facts",
        Door.Procedural or Door.Reasoning => "traces",
        _ => Name(d),
    }).Distinct().Count();
}
