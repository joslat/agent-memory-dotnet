namespace AgentMemory.Abstractions.Options;

/// <summary>
/// The core memory router (PLAN 40.56): which memory kinds a recall reads, chosen per question. Off by default, and
/// restrict-only when on: a kind it does not choose is not read, a kind it chooses keeps its configured cap.
/// </summary>
/// <remarks>
/// A mutable class (the #100 lesson): reachable from a <c>configureMemory</c> lambda. Dark until the accuracy guard
/// (routed against unrouted on a frozen store) passes.
/// </remarks>
public sealed class MemoryRoutingOptions
{
    /// <summary>Off by default: every kind is read on every turn, as before.</summary>
    public bool Enabled { get; set; }

    /// <summary>Use the rules that ship with the library. On by default; off leaves only <see cref="Rules"/>.</summary>
    public bool UseDefaultRules { get; set; } = true;

    /// <summary>
    /// Rules added by the host or a module: a kind, a name for the record, and a pattern. A rule whose pattern matches the
    /// question chooses its kind; this is how a module's own memory section is routed.
    /// </summary>
    public IList<MemoryRoutingRule> Rules { get; } = new List<MemoryRoutingRule>();
}

/// <summary>A routing rule: <paramref name="Pattern"/> (a .NET regular expression, matched case-insensitively) chooses <paramref name="Kind"/>.</summary>
/// <param name="Kind">The memory kind it chooses (<c>facts</c>, <c>graph</c>, <c>preferences</c>, <c>messages</c>, or a module's).</param>
/// <param name="Name">Its name, as the route plan records it.</param>
/// <param name="Pattern">What in the question chooses the kind.</param>
public sealed record MemoryRoutingRule(string Kind, string Name, string Pattern);
