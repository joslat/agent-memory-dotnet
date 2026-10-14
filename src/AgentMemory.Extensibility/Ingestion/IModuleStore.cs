using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>Transactional storage boundary for module-owned records.</summary>
[Experimental("AMEXT001")]
public interface IModuleStore
{
    /// <summary>Writes one provider batch in one transaction and returns one outcome per input index.</summary>
    /// <remarks>Implementations must recheck metadata/migration readiness, admission, actual source
    /// owner/session membership, scoped live targets and version-key concurrency. Expected refusals
    /// return outcomes; a transaction failure throws and must not be described as a proven rollback
    /// after a lost acknowledgement. Core persistence and different provider batches are not atomic.
    /// No concrete module record store is registered by the SDK itself.</remarks>
    Task<IReadOnlyList<ProposalOutcome>> WriteAsync(ModuleWriteBatch batch, CancellationToken cancellationToken = default);
}

/// <summary>Trusted host write inputs, never a model-supplied owner or clock.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleWriteBatch
{
    /// <summary>Resolved owning module.</summary>
    public required string ModuleId { get; init; }
    /// <summary>Frozen metadata identity that the bound store must recognize.</summary>
    public required string ManifestFingerprint { get; init; }
    /// <summary>Host-bound provider identity and permitted trigger.</summary>
    public required IngestionProviderDescriptor Provider { get; init; }
    /// <summary>Frozen source request with policy-resolved owner.</summary>
    public required IngestionRequest Request { get; init; }
    /// <summary>SDK clock captured after proposal generation, or the recorded clock during replay.</summary>
    public required DateTimeOffset AtUtc { get; init; }
    /// <summary>Admitted proposals with their original provider-output positions.</summary>
    public IReadOnlyList<AdmittedModuleProposal> Proposals { get; init; } = [];
}

/// <summary>A frozen admitted proposal and its original provider-output index.</summary>
[Experimental("AMEXT001")]
public sealed record AdmittedModuleProposal
{
    /// <summary>Original zero-based position, retained even when preceding proposals were refused.</summary>
    public required int Index { get; init; }
    /// <summary>Frozen shape-admitted proposal; storage checks remain mandatory.</summary>
    public required ModuleProposal Proposal { get; init; }
}
