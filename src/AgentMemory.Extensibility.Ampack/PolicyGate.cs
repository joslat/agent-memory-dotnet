using System.Diagnostics.CodeAnalysis;
using AgentMemory.Packs.Format;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// The one question every door asks before it acts on a pack (feature 10, R-CORE-139): whether the pack's usage policy
/// (<c>policy.json</c>) lets this host do it now. The doors: <c>read</c> when a pack is verified, <c>export</c> where
/// <c>ampack serve</c> hands records to another program, and <c>rehydrate:&lt;class&gt;</c> where a module set offers records to
/// the host's model (<see cref="Modules.ModuleSetBuilder.UseModelEndpoint"/>).
/// </summary>
[Experimental("AMEXT001")]
public static class PolicyGate
{
    /// <summary>The verbs a usage policy names (pack format registry, <c>policy.verbs</c>).</summary>
    public static IReadOnlySet<string> Verbs => PackRegistry.PolicyVerbs;

    /// <summary>
    /// Whether the pack's usage policy allows <paramref name="verb"/> for this host (the WebIDs of the trust it was loaded under)
    /// at <paramref name="now"/>: deny wins, and an ended rule grants nothing. A pack without a policy allows everything.
    /// </summary>
    /// <exception cref="ArgumentException">The verb is not one a policy names.</exception>
    public static bool Allows(LoadedPack pack, string verb, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (!PackRegistry.PolicyVerbs.Contains(verb)) throw new ArgumentException($"'{verb}' is not a usage-policy verb.", nameof(verb));
        return pack.UsagePolicy is not { } policy || policy.Allows(verb, pack.HostWebIds, now);
    }
}
