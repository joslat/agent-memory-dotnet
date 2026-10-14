using System.Diagnostics.CodeAnalysis;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>Model-dependent proposal producer. The SDK supplies a frozen request with resolved owner.</summary>
[Experimental("AMEXT001")]
public interface IIngestionProvider
{
    /// <summary>Identity and trigger fixed when the host binds this provider.</summary>
    IngestionProviderDescriptor Descriptor { get; }
    /// <summary>Produces serializable proposals without choosing an owner or changing module metadata.</summary>
    Task<IReadOnlyList<ModuleProposal>> ProposeAsync(IngestionRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Host-neutral, ordered module ingestion and provider-free proposal replay.</summary>
[Experimental("AMEXT001")]
public interface IModuleIngestion
{
    /// <summary>Resolves owner, proposes, admits and writes each selected provider in dependency order.</summary>
    Task<IngestionReport> IngestAsync(IngestionRequest request, CancellationToken cancellationToken = default);
    /// <summary>Replays trusted host recordings through admission and storage without calling providers.</summary>
    /// <remarks>Revalidates owner and binds recordings to request/metadata. No whole-agent replay or
    /// exactly-once delivery is promised. Reproducing storage state requires a matching initial graph.</remarks>
    Task<IngestionReport> ReplayAsync(IngestionRequest request, IReadOnlyList<RecordedProposals> recorded,
        CancellationToken cancellationToken = default);
}
