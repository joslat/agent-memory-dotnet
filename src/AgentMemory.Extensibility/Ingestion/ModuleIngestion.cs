using System.Diagnostics;
using AgentMemory.Abstractions.Diagnostics;
using AgentMemory.Extensibility.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
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
    private readonly ModuleIngestionServices _services;

    /// <summary>Binds a successful frozen metadata set to explicit providers and per-module stores.</summary>
    public ModuleIngestion(ModuleResolution resolution, IEnumerable<IIngestionProvider> providers,
        IReadOnlyDictionary<string, IModuleStore> storesByModule, IMemoryIsolationPolicy isolationPolicy,
        TimeProvider timeProvider, ILogger<ModuleIngestion> logger)
        : this(resolution, providers, storesByModule, isolationPolicy, timeProvider, logger, new ModuleIngestionServices())
    {
    }

    /// <summary>
    /// Binds providers with the services store-aware writing needs (Extensibility 0.12, W3): views that show a provider its
    /// existing records, and the closing approver that decides the replacements its proposals name.
    /// </summary>
    public ModuleIngestion(ModuleResolution resolution, IEnumerable<IIngestionProvider> providers,
        IReadOnlyDictionary<string, IModuleStore> storesByModule, IMemoryIsolationPolicy isolationPolicy,
        TimeProvider timeProvider, ILogger<ModuleIngestion> logger, ModuleIngestionServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
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
            if (descriptor.ExistingView is { } view && (!module.Views.Any(v => v.Id == view) || services.Views is null
                || descriptor.MaxExisting is < 1 or > ModuleQuery.MaximumLimit || descriptor.MaxExistingUtf8Bytes < 1))
                throw new ArgumentException($"Provider '{descriptor.Id}' needs existing records from view '{view}': the view must be "
                    + "declared by its module, views must be supplied, and its limits must be positive.", nameof(providers));
            entries.Add(new(module.Id, module.Version.ToString(), descriptor with { }, provider, store));
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
            using var activity = StartActivity(entry, "ingest");
            try
            {
                RecordedProposals batch;
                try
                {
                    var shown = resolved with { Existing = await ExistingAsync(entry, resolved, cancellationToken).ConfigureAwait(false) };
                    var proposed = await entry.Provider.ProposeAsync(shown, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    var proposals = ModuleIngestionSnapshot.Proposals(proposed);
                    var atUtc = _clock.GetUtcNow().ToUniversalTime();
                    batch = new() { Provider = entry.Descriptor, ManifestFingerprint = _resolution.ManifestFingerprint!,
                        RequestFingerprint = fingerprint, AtUtc = atUtc, Proposals = proposals,
                        Closings = await ClosingsAsync(entry, shown, proposals, atUtc, cancellationToken).ConfigureAwait(false) };
                    recorded.Add(batch);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    _logger.LogWarning(exception, "Module provider {ProviderId} failed before storage.", entry.Descriptor.Id);
                    outcomes.Add(Observe(activity, Unavailable(entry, "Provider generation or recording failed; no batch was written.", [])));
                    continue;
                }
                outcomes.Add(Observe(activity, await WriteAsync(entry, resolved, batch, cancellationToken).ConfigureAwait(false)));
            }
            catch (OperationCanceledException)
            {
                ObserveCancellation(activity);
                throw;
            }
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
            if (record.Closings is null || record.Closings.Any(c => c is null || c.ProposalIndex < 0 || c.ProposalIndex >= (record.Proposals?.Count ?? 0)
                    || string.IsNullOrWhiteSpace(c.Key) || !Enum.IsDefined(c.Decision)))
                throw new ArgumentException("Replay closings must name recorded proposals, keys and known decisions.", nameof(recorded));
            try { byId.Add(record.Provider.Id, record with { Provider = entry.Descriptor, Proposals = ModuleIngestionSnapshot.Proposals(record.Proposals),
                Closings = record.Closings.Select(c => c with { }).ToList().AsReadOnly() }); }
            catch (Exception exception) when (exception is ArgumentException or ObjectDisposedException or InvalidOperationException)
            { throw new ArgumentException("Replay proposals cannot be captured independently.", nameof(recorded), exception); }
        }
        var batches = new List<RecordedProposals>(); var outcomes = new List<ProviderIngestionOutcome>();
        foreach (var entry in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byId.TryGetValue(entry.Descriptor.Id, out var batch)) continue;
            batches.Add(batch);
            using var activity = StartActivity(entry, "replay");
            try
            {
                outcomes.Add(Observe(activity, await WriteAsync(entry, resolved, batch, cancellationToken).ConfigureAwait(false)));
            }
            catch (OperationCanceledException)
            {
                ObserveCancellation(activity);
                throw;
            }
        }
        return Report(resolved.Owner, batches, outcomes);
    }

    private Activity? StartActivity(Entry entry, string operation)
    {
        var activity = AgentMemoryDiagnostics.Source.StartActivity(ModuleTelemetry.Ingest);
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(ModuleTelemetry.ModuleId, entry.ModuleId);
            activity.SetTag(ModuleTelemetry.ModuleVersion, entry.ModuleVersion);
            activity.SetTag(ModuleTelemetry.ProviderId, entry.Descriptor.Id);
            activity.SetTag(ModuleTelemetry.Fingerprint, _resolution.ManifestFingerprint);
            activity.SetTag(ModuleTelemetry.FingerprintKind, "metadata");
            activity.SetTag(ModuleTelemetry.Operation, operation);
        }
        return activity;
    }

    private static ProviderIngestionOutcome Observe(Activity? activity, ProviderIngestionOutcome outcome)
    {
        if (activity is { IsAllDataRequested: true })
        {
            activity.SetTag(ModuleTelemetry.Outcome, outcome.Status.ToString());
            activity.SetTag(ModuleTelemetry.Items, outcome.Proposals.Count);
            activity.SetTag(ModuleTelemetry.Refused, outcome.Proposals.Count(p => p.Status == ProposalWriteStatus.Refused));
            activity.SetStatus(outcome.Status == ProviderIngestionStatus.Unavailable ? ActivityStatusCode.Error : ActivityStatusCode.Ok);
        }
        return outcome;
    }

    private static void ObserveCancellation(Activity? activity)
    {
        if (activity is not { IsAllDataRequested: true }) return;
        activity.SetTag(ModuleTelemetry.Outcome, "Cancelled");
        activity.SetStatus(ActivityStatusCode.Error);
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
        // An approved replacement closes its record in the replacing proposal's own transaction, and only when that proposal
        // is admitted: never a closing without the record that replaces it.
        var closingOf = new Dictionary<int, int>();
        foreach (var closing in batch.Closings.Where(c => c.Decision == ClosingDecision.Approved))
        {
            var replacing = admitted.FirstOrDefault(a => a.Index == closing.ProposalIndex);
            if (replacing is null) continue;
            var invalidation = new ModuleProposal
            {
                Type = replacing.Proposal.Type, Key = closing.Key, Kind = ProposalKind.Invalidate, Source = replacing.Proposal.Source,
            };
            var result = ModuleProposalAdmission.Admit(_resolution, entry.Descriptor, request, invalidation);
            if (!result.Accepted) continue;
            var position = batch.Proposals.Count + closingOf.Count;
            closingOf[position] = closing.ProposalIndex;
            admitted.Add(new() { Index = position, Proposal = result.Proposal! });
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
                var receipts = ValidateOutcomes(stored, admitted);
                var closed = receipts.Where(o => closingOf.ContainsKey(o.Index) && o.Status == ProposalWriteStatus.Invalidated)
                    .ToLookup(o => closingOf[o.Index], o => admitted.Single(a => a.Index == o.Index).Proposal.Key);
                outcomes.AddRange(receipts.Where(o => !closingOf.ContainsKey(o.Index)).Select(o => closed[o.Index].Any()
                    ? o with { Closed = closed[o.Index].ToList().AsReadOnly() } : o));
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

    /// <summary>The provider's existing records from its declared view, for the resolved owner, within its limits.</summary>
    private async Task<IReadOnlyList<ModuleViewItem>> ExistingAsync(Entry entry, IngestionRequest request, CancellationToken cancellationToken)
    {
        if (entry.Descriptor.ExistingView is not { } view) return [];
        try
        {
            var result = await _services.Views!(entry.ModuleId).EvaluateAsync(view, new ModuleReadContext
            {
                Scope = request.Owner is null ? null : MemoryScope.For(request.Owner), SessionId = request.SessionId,
            }, cancellationToken).ConfigureAwait(false);
            if (result.Status != ModuleReadStatus.Succeeded)
            {
                _logger.LogWarning("Module provider {ProviderId} sees no existing records: its view returned {Status}.", entry.Descriptor.Id, result.Status);
                return [];
            }
            var shown = new List<ModuleViewItem>();
            var bytes = 0;
            foreach (var item in result.Items.Take(entry.Descriptor.MaxExisting))
            {
                bytes += System.Text.Encoding.UTF8.GetByteCount(item.Text);
                if (bytes > entry.Descriptor.MaxExistingUtf8Bytes) break;
                shown.Add(item);
            }
            return shown.AsReadOnly();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "Module provider {ProviderId} sees no existing records: its view failed.", entry.Descriptor.Id);
            return [];
        }
    }

    /// <summary>
    /// Decides every replacement the proposals name, in one approver call for the batch. Only an admitted assertion's
    /// replacements of records the provider was shown are asked; the rest are recorded as not shown or unjudged.
    /// </summary>
    private async Task<IReadOnlyList<RecordedClosing>> ClosingsAsync(Entry entry, IngestionRequest request,
        IReadOnlyList<ModuleProposal> proposals, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        var named = new List<(int Index, string Key, MemoryUpdatePair? Pair)>();
        var types = _resolution.Modules.Single(m => m.Id == entry.ModuleId).Ontology?.Types ?? [];
        for (var index = 0; index < proposals.Count; index++)
        {
            var proposal = proposals[index];
            if (proposal.Replaces is not { Count: > 0 }
                || !ModuleProposalAdmission.Admit(_resolution, entry.Descriptor, request, proposal).Accepted) continue;
            var type = types.Single(t => t.Name == proposal.Type);
            foreach (var key in proposal.Replaces)
            {
                var stored = request.Existing.FirstOrDefault(e => e.Record.Type == proposal.Type && e.Record.Key == key);
                named.Add((index, key, stored is null ? null
                    : new MemoryUpdatePair(index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + key,
                        ModuleRenderTemplate.Render(type, proposal.Properties), stored.Text)));
            }
        }
        if (named.Count == 0) return [];
        IReadOnlyDictionary<string, MemoryClosingVerdict>? verdicts = null;
        var pairs = named.Where(n => n.Pair is not null).Select(n => n.Pair!).ToList();
        if (pairs.Count > 0 && _services.Closings is { } approver && approver.CanApprove(named: true))
        {
            try
            {
                var said = string.Join("\n", request.Messages.Where(m => m.Role == "user").Select(m => m.Text));
                verdicts = await approver.ApproveAsync(new MemoryUpdateRequest(said.Length == 0 ? null : said, atUtc, pairs) { Named = true },
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "The closing approver failed for {ProviderId}; its replacements close nothing.", entry.Descriptor.Id);
            }
        }
        return named.Select(n =>
        {
            if (n.Pair is null) return new RecordedClosing { ProposalIndex = n.Index, Key = n.Key, Decision = ClosingDecision.NotShown };
            if (verdicts is null || !verdicts.TryGetValue(n.Pair.Key, out var verdict) || verdict is null)
                return new RecordedClosing { ProposalIndex = n.Index, Key = n.Key, Decision = ClosingDecision.Unjudged };
            return new RecordedClosing
            {
                ProposalIndex = n.Index, Key = n.Key, Probability = verdict.Probability,
                Decision = verdict.Approved ? ClosingDecision.Approved
                    : verdict.Probability is null ? ClosingDecision.Unjudged : ClosingDecision.Declined,
            };
        }).ToList().AsReadOnly();
    }

    private static IngestionReport Denied() => new() { Denied = true, Errors = ["Owner isolation policy denied module ingestion."] };
    private static IngestionReport Report(string? owner, List<RecordedProposals> records, List<ProviderIngestionOutcome> outcomes) =>
        new() { ResolvedOwner = owner, Recorded = records.AsReadOnly(), Providers = outcomes.AsReadOnly() };
    private static ProviderIngestionOutcome Unavailable(Entry entry, string detail, List<ProposalOutcome> known) =>
        new() { ProviderId = entry.Descriptor.Id, Status = ProviderIngestionStatus.Unavailable, Detail = detail,
            Proposals = known.OrderBy(o => o.Index).ToList().AsReadOnly() };
    private sealed record Entry(string ModuleId, string ModuleVersion, IngestionProviderDescriptor Descriptor, IIngestionProvider Provider, IModuleStore Store);
}

/// <summary>What store-aware module writing needs from the host (Extensibility 0.12, W3); every member is optional.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleIngestionServices
{
    /// <summary>Evaluates a module's declared views, by module id: a provider's existing records come from here.</summary>
    public Func<string, IModuleViews>? Views { get; init; }

    /// <summary>Decides the replacements proposals name. Without it no replacement closes anything.</summary>
    public IMemoryClosingApprover? Closings { get; init; }
}
