namespace AgentMemory.Abstractions.Domain;

/// <summary>
/// What the memory router chose for one question (PLAN 40.56): whether to recall at all, and which kinds. Recorded on the
/// route plan, so "why this answer" can say why a kind was not read.
/// </summary>
public sealed record MemoryRoute
{
    /// <summary>Facts (and the shared knowledge stored as facts).</summary>
    public const string Facts = "facts";

    /// <summary>Entities, their relationships, and GraphRAG.</summary>
    public const string Graph = "graph";

    /// <summary>Preferences.</summary>
    public const string Preferences = "preferences";

    /// <summary>The current session's relevant messages.</summary>
    public const string Messages = "messages";

    /// <summary>The kinds the core router chooses between.</summary>
    public static IReadOnlyList<string> CoreKinds { get; } = [Facts, Graph, Preferences, Messages];

    /// <summary>False when the question needs no memory (a telling, a correction): nothing is read.</summary>
    public required bool Recall { get; init; }

    /// <summary>The kinds read, in <see cref="CoreKinds"/> order; empty when <see cref="Recall"/> is false.</summary>
    public IReadOnlyList<string> Kinds { get; init; } = [];

    /// <summary>For each kind read, the rule that chose it.</summary>
    public IReadOnlyDictionary<string, string> ChosenBy { get; init; } = new Dictionary<string, string>();

    /// <summary>Why nothing was read, when <see cref="Recall"/> is false.</summary>
    public string? SkipReason { get; init; }
}
