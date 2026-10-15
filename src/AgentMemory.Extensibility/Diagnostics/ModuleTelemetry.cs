using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Diagnostics;

/// <summary>Experimental module span and tag names on the shared AgentMemory diagnostics source.</summary>
/// <remarks>No source, listener, provider or exporter is installed by these constants.</remarks>
[Experimental("AMEXT001")]
public static class ModuleTelemetry
{
    /// <summary>Compilation of explicitly bound module contributions.</summary>
    public const string Compile = "memory.module.compile";
    /// <summary>One explicitly bound contributor invocation.</summary>
    public const string Contribute = "memory.module.contribute";
    /// <summary>One selected provider's ingestion or recorded replay attempt.</summary>
    public const string Ingest = "memory.module.ingest";
    /// <summary>Explicit module migration planning or application.</summary>
    public const string Migrate = "memory.module.migrate";
    /// <summary>Host-bound manifest identity; never an owner or source-message identity.</summary>
    public const string ModuleId = "memory.module.id";
    /// <summary>Frozen resolved manifest version, not a record version.</summary>
    public const string ModuleVersion = "memory.module.version";
    /// <summary>Resolved declaration fingerprint. This is metadata, not a signed or activated set identity.</summary>
    public const string Fingerprint = "memory.module.fingerprint";
    /// <summary>Kind of fingerprint; static resolution is labelled metadata.</summary>
    public const string FingerprintKind = "memory.module.fingerprint.kind";
    /// <summary>Operation within a span family, such as ingest or replay.</summary>
    public const string Operation = "memory.module.operation";
    /// <summary>Stable outcome name; no raw exception text or content.</summary>
    public const string Outcome = "memory.module.outcome";
    /// <summary>Known item outcomes. Unavailable ingestion can have fewer known outcomes than attempted proposals.</summary>
    public const string Items = "memory.module.items";
    /// <summary>Known refused item outcomes; a subset of Items, not an inferred failed-write count.</summary>
    public const string Refused = "memory.module.refused";
    /// <summary>Distinct bound modules with at least one non-excluded contributor, before applicability.</summary>
    public const string ModuleCount = "memory.module.count";
    /// <summary>Frozen host-bound contributor identity.</summary>
    public const string ContributorId = "memory.contributor.id";
    /// <summary>Compile event recording an omission identity and reason, without private details.</summary>
    public const string Omission = "memory.module.omission";
    /// <summary>Frozen host-bound provider identity.</summary>
    public const string ProviderId = "memory.provider.id";
}
