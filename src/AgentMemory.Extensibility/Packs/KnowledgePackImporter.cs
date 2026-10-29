using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Packs;

/// <summary>
/// Stores knowledge pack records (Extensibility 0.13 §K3). The only writer of pack provenance: no ingestion provider receives
/// this interface, so a turn can never write a record that claims to come from a pack.
/// </summary>
[Experimental("AMEXT001")]
public interface IKnowledgePackStore
{
    /// <summary>What the store holds of a pack now.</summary>
    Task<KnowledgePackState> GetStateAsync(ModuleManifest pack, CancellationToken cancellationToken = default);

    /// <summary>
    /// Applies the pack's schema, then writes its records in one transaction: new keys are created, changed records get a new
    /// version, equal ones are no-ops, and live records of the pack this version no longer holds are closed.
    /// </summary>
    Task<KnowledgePackImportResult> ImportAsync(KnowledgePack pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default);

    /// <summary>Closes every live record of the pack. History and schema stay; a later import reopens the keys.</summary>
    Task<KnowledgePackImportResult> RemoveAsync(ModuleManifest pack, DateTimeOffset atUtc, CancellationToken cancellationToken = default);
}

/// <summary>What a store holds of a pack.</summary>
[Experimental("AMEXT001")]
public sealed record KnowledgePackState
{
    /// <summary>The pack id.</summary>
    public required string PackId { get; init; }
    /// <summary>The highest version among its live records; null when none is live.</summary>
    public string? Version { get; init; }
    /// <summary>How many of its records are live.</summary>
    public int LiveRecords { get; init; }
}

/// <summary>What an import or a removal changed.</summary>
[Experimental("AMEXT001")]
public sealed record KnowledgePackImportResult
{
    /// <summary>The pack id.</summary>
    public required string PackId { get; init; }
    /// <summary>The imported version; null for a removal.</summary>
    public string? Version { get; init; }
    /// <summary>The imported bundle digest; null for a removal.</summary>
    public string? Digest { get; init; }
    /// <summary>Records written for keys that had no live record.</summary>
    public int Created { get; init; }
    /// <summary>Records written as a new version of a live record.</summary>
    public int Changed { get; init; }
    /// <summary>Records equal to the live version, so not written.</summary>
    public int Unchanged { get; init; }
    /// <summary>Live records closed: gone from this version, or removed with the pack.</summary>
    public int Closed { get; init; }
}

/// <summary>
/// Imports and removes knowledge packs (Extensibility 0.13 §K3). Host code or an operator calls it, never a turn, a model or
/// an MCP tool. A pack older than the one stored is refused unless a downgrade is asked for.
/// </summary>
[Experimental("AMEXT001")]
public sealed class KnowledgePackImporter
{
    private readonly IKnowledgePackStore _store;
    private readonly TimeProvider _clock;

    /// <summary>Binds the importer to the host's pack store.</summary>
    public KnowledgePackImporter(IKnowledgePackStore store, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>What the store holds of a pack now.</summary>
    public Task<KnowledgePackState> GetStateAsync(ModuleManifest pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return _store.GetStateAsync(pack, cancellationToken);
    }

    /// <summary>Imports a verified pack; equal records are no-ops, so importing the same version again writes nothing.</summary>
    public async Task<KnowledgePackImportResult> ImportAsync(KnowledgePack pack, bool allowDowngrade = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        // A pack past its validUntil writes nothing, on this path too (review 2, M9).
        if (pack.ValidUntil is { } until && _clock.GetUtcNow() > until)
            throw new KnowledgePackException([$"{pack.Id} {pack.Version} was valid until {until:u}; it is not imported."]);
        var state = await _store.GetStateAsync(pack.Manifest, cancellationToken).ConfigureAwait(false);
        if (!allowDowngrade && state.Version is { } stored && ModuleVersion.Parse(stored).CompareTo(pack.Manifest.Version) > 0)
            throw new KnowledgePackException([$"The store holds {pack.Id} {stored}, newer than {pack.Version}; ask for a downgrade explicitly."]);
        return await _store.ImportAsync(pack, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Closes every live record of a pack.</summary>
    public Task<KnowledgePackImportResult> RemoveAsync(ModuleManifest pack, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pack);
        return _store.RemoveAsync(pack, _clock.GetUtcNow(), cancellationToken);
    }
}
