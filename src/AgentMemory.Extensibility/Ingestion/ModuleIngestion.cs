using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Modules;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.Ingestion;

/// <summary>Explicitly composed ingestion engine that invokes only host-bound providers.</summary>
/// <remarks>The host supplies authorized provider objects and a store binding for each participating module.
/// Metadata resolution alone is not a trust or runtime capability decision. No retries are performed.
/// Each provider batch is awaited before the next provider; earlier progress survives later failures.</remarks>
[Experimental("AMEXT001")]
public sealed class ModuleIngestion : IModuleIngestion
{
    private readonly ModuleResolution _resolution;
    private readonly IReadOnlyList<Entry> _entries;
    private readonly IMemoryIsolationPolicy _isolation;
    private readonly TimeProvider _clock;
    private readonly ILogger<ModuleIngestion> _logger;

    /// <summary>Binds a successful frozen metadata set to explicit providers and per-module stores.</summary>
    public ModuleIngestion(ModuleResolution resolution, IEnumerable<IIngestionProvider> providers,
        IReadOnlyDictionary<string, IModuleStore> storesByModule, IMemoryIsolationPolicy isolationPolicy,
        TimeProvider timeProvider, ILogger<ModuleIngestion> logger)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(storesByModule);
        ArgumentNullException.ThrowIfNull(isolationPolicy);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        if (!resolution.Succeeded) throw new ArgumentException("Successful metadata resolution is required.", nameof(resolution));
        _resolution = resolution; _isolation = isolationPolicy; _clock = timeProvider; _logger = logger;
        var stores = storesByModule.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        if (stores.Any(p => p.Value is null || !resolution.Modules.Any(m => m.Id == p.Key)))
            throw new ArgumentException("Store bindings must name resolved modules and non-null stores.", nameof(storesByModule));
        var entries = new List<Entry>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in providers)
        {
            if (provider?.Descriptor is not { } descriptor) throw new ArgumentException("Provider descriptor is required.", nameof(providers));
            var module = resolution.Modules.FirstOrDefault(m => ModuleIdentity.IsOwnedId(m.Id, descriptor.Id));
            if (module is null || !seen.Add(descriptor.Id) || descriptor.Trigger is not (IngestionTrigger.EveryTurn or IngestionTrigger.OnRequest))
                throw new ArgumentException("Providers require distinct owned identities and supported triggers.", nameof(providers));
            if (!stores.TryGetValue(module.Id, out var store)) throw new ArgumentException("Every provider module requires an explicit store binding.", nameof(storesByModule));
            entries.Add(new(module.Id, descriptor with { }, provider, store));
        }
        _entries = resolution.Modules.SelectMany(m => entries.Where(e => e.ModuleId == m.Id)
            .OrderBy(e => e.Descriptor.Id, StringComparer.Ordinal)).ToList().AsReadOnly();
    }

    /// <inheritdoc/>
    public async Task<IngestionReport> IngestAsync(IngestionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.Count == 0 && request.Trigger == IngestionTrigger.EveryTurn && request.ProviderIds is { Count: 0 }) return new();
        var resolved = Resolve(request);
        if (resolved is null) return Denied();
        cancellationToken.ThrowIfCancellationRequested();
        var selected = Select(resolved);
        var fingerprint = ModuleIngestionSnapshot.Fingerprint(resolved);
        var recorded = new List<RecordedProposals>(); var outcomes = new List<ProviderIngestionOutcome>();
        foreach (var entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RecordedProposals batch;
            try
            {
                var proposed = await entry.Provider.ProposeAsync(resolved, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                var proposals = ModuleIngestionSnapshot.Proposals(proposed);
                batch = new() { Provider = entry.Descriptor, ManifestFingerprint = _resolution.ManifestFingerprint!,
                    RequestFingerprint = fingerprint, AtUtc = _clock.GetUtcNow().ToUniversalTime(), Proposals = proposals };
                recorded.Add(batch);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Module provider {ProviderId} failed before storage.", entry.Descriptor.Id);
                outcomes.Add(Unavailable(entry, "Provider generation or recording failed; no batch was written.", []));
                continue;
            }
            outcomes.Add(await WriteAsync(entry, resolved, batch, cancellationToken).ConfigureAwait(false));
        }
        return Report(resolved.Owner, recorded, outcomes);
    }

    /// <inheritdoc/>
    public async Task<IngestionReport> ReplayAsync(IngestionRequest request, IReadOnlyList<RecordedProposals> recorded,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(recorded);
        cancellationToken.ThrowIfCancellationRequested();
        if (_entries.Count == 0 && recorded.Count == 0 && request.Trigger == IngestionTrigger.EveryTurn && request.ProviderIds is { Count: 0 }) return new();
        var resolved = Resolve(request);
        if (resolved is null) return Denied();
        cancellationToken.ThrowIfCancellationRequested();
        var selected = Select(resolved);
        var fingerprint = ModuleIngestionSnapshot.Fingerprint(resolved);
        var byId = new Dictionary<string, RecordedProposals>(StringComparer.Ordinal);
        // Validate and snapshot the entire replay before the first write, including later batches.
        foreach (var record in recorded)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = selected.FirstOrDefault(e => e.Descriptor.Id == record?.Provider?.Id);
            if (record is null || entry is null || record.Provider != entry.Descriptor
                || record.ManifestFingerprint != _resolution.ManifestFingerprint || record.RequestFingerprint != fingerprint
                || record.AtUtc.Offset != TimeSpan.Zero || byId.ContainsKey(record.Provider.Id))
                throw new ArgumentException("Replay batch must uniquely match the selected provider, metadata, resolved request and UTC clock.", nameof(recorded));
            try { byId.Add(record.Provider.Id, record with { Provider = entry.Descriptor, Proposals = ModuleIngestionSnapshot.Proposals(record.Proposals) }); }
            catch (Exception exception) when (exception is ArgumentException or ObjectDisposedException or InvalidOperationException)
            { throw new ArgumentException("Replay proposals cannot be captured independently.", nameof(recorded), exception); }
        }
        var batches = new List<RecordedProposals>(); var outcomes = new List<ProviderIngestionOutcome>();
        foreach (var entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(entry.Descriptor.Id, out var batch)) continue;
            batches.Add(batch);
            outcomes.Add(await WriteAsync(entry, resolved, batch, cancellationToken).ConfigureAwait(false));
        }
        return Report(resolved.Owner, batches, outcomes);
    }

    private IngestionRequest? Resolve(IngestionRequest request)
    {
        string? owner;
        try { owner = _isolation.ResolveWriteOwner(request.Owner, "module.ingest", MemoryOperationAccess.Tenant); }
        catch (MemoryOwnerScopeRequiredException) { return null; }
        return ModuleIngestionSnapshot.Request(request, owner);
    }

    private IReadOnlyList<Entry> Select(IngestionRequest request)
    {
        if (request.Trigger == IngestionTrigger.EveryTurn)
            return _entries.Where(e => e.Descriptor.Trigger == IngestionTrigger.EveryTurn).ToList();
        if (request.ProviderIds.Any(id => !_entries.Any(e => e.Descriptor.Id == id)))
            throw new ArgumentException("Explicit selection names an unbound provider.", nameof(request));
        return _entries.Where(e => request.ProviderIds.Contains(e.Descriptor.Id, StringComparer.Ordinal)).ToList();
    }

    private async Task<ProviderIngestionOutcome> WriteAsync(Entry entry, IngestionRequest request,
        RecordedProposals batch, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var outcomes = new List<ProposalOutcome>(); var admitted = new List<AdmittedModuleProposal>();
        for (var index = 0; index < batch.Proposals.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = ModuleProposalAdmission.Admit(_resolution, entry.Descriptor, request, batch.Proposals[index]);
            if (result.Accepted) admitted.Add(new() { Index = index, Proposal = result.Proposal! });
            else outcomes.Add(new() { Index = index, Status = ProposalWriteStatus.Refused, Errors = result.Errors });
        }
        if (admitted.Count > 0)
        {
            try
            {
                var stored = await entry.Store.WriteAsync(new()
                {
                    ModuleId = entry.ModuleId, ManifestFingerprint = _resolution.ManifestFingerprint!,
                    Provider = entry.Descriptor, Request = request, AtUtc = batch.AtUtc,
                    Proposals = admitted.AsReadOnly()
                }, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                outcomes.AddRange(ValidateOutcomes(stored, admitted));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Module store failed for {ProviderId}; commit status is unknown.", entry.Descriptor.Id);
                return Unavailable(entry, "Store failed or returned an invalid receipt; commit status is unknown.", outcomes);
            }
        }
        var refused = outcomes.Count(o => o.Status == ProposalWriteStatus.Refused);
        return new() { ProviderId = entry.Descriptor.Id, Status = refused == 0 ? ProviderIngestionStatus.Succeeded
            : refused == outcomes.Count ? ProviderIngestionStatus.Refused : ProviderIngestionStatus.Partial,
            Proposals = outcomes.OrderBy(o => o.Index).ToList().AsReadOnly() };
    }

    private static IReadOnlyList<ProposalOutcome> ValidateOutcomes(IReadOnlyList<ProposalOutcome> stored,
        IReadOnlyList<AdmittedModuleProposal> admitted)
    {
        if (stored is null || stored.Count != admitted.Count || stored.Any(o => o is null)
            || !stored.Select(o => o.Index).Order().SequenceEqual(admitted.Select(p => p.Index).Order())
            || stored.Any(o => !Enum.IsDefined(o.Status) || o.Errors is null || o.Errors.Any(string.IsNullOrWhiteSpace)
                || (o.Status == ProposalWriteStatus.Refused ? o.Errors.Count == 0 : o.Errors.Count != 0)
                || (o.Status is ProposalWriteStatus.Stored or ProposalWriteStatus.Invalidated && string.IsNullOrWhiteSpace(o.RecordId))))
            throw new InvalidOperationException("Store receipt must provide exactly one valid outcome for each admitted proposal.");
        return stored.Select(o => o with { Errors = o.Errors.ToList().AsReadOnly() }).ToList().AsReadOnly();
    }

    private static IngestionReport Denied() => new() { Denied = true, Errors = ["Owner isolation policy denied module ingestion."] };
    private static IngestionReport Report(string? owner, List<RecordedProposals> records, List<ProviderIngestionOutcome> outcomes) =>
        new() { ResolvedOwner = owner, Recorded = records.AsReadOnly(), Providers = outcomes.AsReadOnly() };
    private static ProviderIngestionOutcome Unavailable(Entry entry, string detail, List<ProposalOutcome> known) =>
        new() { ProviderId = entry.Descriptor.Id, Status = ProviderIngestionStatus.Unavailable, Detail = detail,
            Proposals = known.OrderBy(o => o.Index).ToList().AsReadOnly() };
    private sealed record Entry(string ModuleId, IngestionProviderDescriptor Descriptor, IIngestionProvider Provider, IModuleStore Store);
}
