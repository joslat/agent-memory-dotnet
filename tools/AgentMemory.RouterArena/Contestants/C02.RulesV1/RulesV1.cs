using AgentMemory.Abstractions.Domain;
using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 2 · Rules v1: the core rule router as it is in the library (<c>RuleBasedMemoryRouter</c>, called, not copied). Wording
/// rules choose among its four kinds (facts, graph, preferences, messages), so it can open only the semantic, entity-graph,
/// preference and episodic doors (derived facts ride with the facts); a turn with no question mark and no request reads
/// nothing.
/// </summary>
public sealed class RulesV1(bool rewrite = false) : IContestant
{
    public string Family => "rules";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?> { ["rewrite"] = rewrite };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        var route = context.Data.Rules.Route(item.Text);
        return route.Recall ? context.Open(route.Kinds.SelectMany(DoorsOfRoute), rewrite) : Decision.Nothing();
    }

    /// <summary>The doors a route kind of the core router reads.</summary>
    internal static IEnumerable<Door> DoorsOfRoute(string kind) => kind switch
    {
        MemoryRoute.Facts => [Door.Semantic, Door.Derived],
        MemoryRoute.Graph => [Door.EntityGraph],
        MemoryRoute.Preferences => [Door.Preference],
        MemoryRoute.Messages => [Door.Episodic],
        _ => [],
    };
}
