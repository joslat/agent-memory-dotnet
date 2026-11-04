// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Pure metadata resolution result. Success is not activation or publisher authorization.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleResolution
{
    internal ModuleResolution(IReadOnlyList<ModuleManifest> modules, IReadOnlyList<string> errors, string? fingerprint)
    { Modules = modules; Errors = errors; ManifestFingerprint = fingerprint; }
    /// <summary>Deep-frozen declarations in dependency order; empty on any validation error.</summary>
    public IReadOnlyList<ModuleManifest> Modules { get; }
    /// <summary>All diagnosed problems in ordinal order.</summary>
    public IReadOnlyList<string> Errors { get; }
    /// <summary>Whether metadata checks succeeded; does not attest actual host registration or trust.</summary>
    public bool Succeeded => Errors.Count == 0;
    /// <summary>SHA-256 of validated metadata and resolution scope, or null on failure.</summary>
    /// <remarks>A profile store projection also binds its whole composition and store alias.
    /// This is not the eventual active-set fingerprint: configuration, migration
    /// and trust identities must be added by composition before activation.</remarks>
    public string? ManifestFingerprint { get; }

    /// <summary>
    /// The host's consent grants for this resolution (PACK-consent): links and walks into a module that does not list the
    /// linking module in its containableBy are allowed only for these pairs. Empty by default.
    /// </summary>
    public IReadOnlyList<ModuleConsentGrant> ConsentGrants { get; init; } = [];

    /// <summary>
    /// Why <paramref name="module"/> may not reach another module's records through <paramref name="relation"/>, or null when
    /// it may: the other module's consent (its containableBy covers this one) or a <see cref="ConsentGrants"/> entry. Walks
    /// and imports apply the same rule.
    /// </summary>
    public string? ConsentRefusal(ModuleManifest module, ModuleRelation relation)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(relation);
        return ModuleConsent.Refusal(module, relation, id => Modules.FirstOrDefault(m => m.Id == id), ConsentGrants);
    }
}
