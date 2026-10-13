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
    /// <summary>SHA-256 of normalized manifest metadata and target SDK, or null on failure.</summary>
    /// <remarks>This is not the eventual active-set fingerprint: profile, configuration, migration
    /// and trust identities must be added by composition before activation.</remarks>
    public string? ManifestFingerprint { get; }
}
