// SPDX-License-Identifier: Apache-2.0
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
    /// <summary>Store reads one traversal walk made, against its budget of 128.</summary>
    public const string TraversalReads = "memory.traversal.reads";
    /// <summary>Records one traversal walk visited, against its budget of 256.</summary>
    public const string TraversalVisited = "memory.traversal.visited";
    /// <summary>Milliseconds one traversal walk took, against its budget of 5 seconds.</summary>
    public const string TraversalElapsedMs = "memory.traversal.elapsed_ms";
    /// <summary>The budget that stopped a traversal walk (<c>visited</c>, <c>reads</c> or <c>time</c>), or <c>none</c>.</summary>
    public const string TraversalStoppedBy = "memory.traversal.stopped_by";
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
    /// <summary>Owned definitions attempted by one step span; one even when the attempt fails, not statements or writes.</summary>
    public const string MigrationSteps = "memory.module.migration.steps";
    /// <summary>Pending definitions in the returned migration assessment, not statement or record counts.</summary>
    public const string MigrationsPending = "memory.module.migration.pending";
    /// <summary>Applied definitions in the returned assessment, including previously applied definitions.</summary>
    public const string MigrationsApplied = "memory.module.migration.applied";
    /// <summary>Number of assessed problems; diagnostic text is never included.</summary>
    public const string MigrationProblems = "memory.module.migration.problems";
    /// <summary>Frozen host-bound provider identity.</summary>
    public const string ProviderId = "memory.provider.id";
    /// <summary>One pack import, removal, erasure, mount or detach.</summary>
    public const string Pack = "memory.pack";
    /// <summary>One step inside a pack operation (schema, records, meaning index, local copy).</summary>
    public const string PackStep = "memory.pack.step";
    /// <summary>The pack operation (see <see cref="Packs.PackOperations"/>).</summary>
    public const string PackOperation = "memory.pack.operation";
    /// <summary>The step a pack span ran, or the step a pack failure happened at (see <see cref="Packs.PackSteps"/>).</summary>
    public const string PackStepName = "memory.pack.step.name";
    /// <summary>The pack id; a pack's id, version and digest are public metadata, never owner data.</summary>
    public const string PackId = "memory.pack.id";
    /// <summary>The pack version.</summary>
    public const string PackVersion = "memory.pack.version";
    /// <summary>The pack's bundle digest.</summary>
    public const string PackDigest = "memory.pack.digest";
    /// <summary>The <see cref="Packs.PackFailure.Code"/> of a failed pack operation or step; no exception text.</summary>
    public const string PackFailureCode = "memory.pack.failure.code";
}
