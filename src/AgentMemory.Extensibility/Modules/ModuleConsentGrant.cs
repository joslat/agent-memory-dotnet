// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>
/// A host's grant (PACK-consent): <see cref="Module"/> may link into, and walk into, the records of <see cref="Into"/>
/// although <see cref="Into"/> does not list it in <see cref="ModuleManifest.ContainableBy"/>. The publisher's consent is
/// the rule; this is the host overriding it for its own store, by name, and the set fingerprint names every grant.
/// </summary>
/// <param name="Module">The module whose relation reaches into the other.</param>
/// <param name="Into">The module it reaches into.</param>
[Experimental("AMEXT001")]
public sealed record ModuleConsentGrant(string Module, string Into);

/// <summary>Whether a module may reach another's records through a relation: the other's consent, or the host's grant.</summary>
[Experimental("AMEXT001")]
internal static class ModuleConsent
{
    /// <summary>
    /// Why <paramref name="module"/> may not reach through <paramref name="relation"/>, or null when it may: a relation into
    /// another module needs that module's <see cref="ModuleManifest.ContainableBy"/> to cover this one (red-team M4), or a
    /// host grant. The same rule for a walk and for a link written at import.
    /// </summary>
    internal static string? Refusal(ModuleManifest module, ModuleRelation relation, Func<string, ModuleManifest?> find,
        IReadOnlyList<ModuleConsentGrant> grants)
    {
        var parts = relation.To.Split(':');
        if (parts.Length != 2 || parts[0] == "core" || parts[0] == module.Id) return null;
        if (grants.Any(g => g.Module == module.Id && g.Into == parts[0])) return null;
        var consents = find(parts[0])?.ContainableBy?.Any(c => ModuleSetBuilder.Covers(c, module.Id)) == true;
        return consents ? null : $"{module.Id}: {relation.Name} reaches into {parts[0]}, which does not list it in containableBy.";
    }
}
