using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Migrations;
using AgentMemory.Extensibility.Neo4j.Queries;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>Explicit module record writer using one Neo4j transaction per admitted provider batch.</summary>
/// <remarks>Never applies migrations. Source membership, current-version locking and link resolution
/// run inside the write transaction. A transactional invariant refusal rolls back the entire admitted
/// batch; unexpected failures propagate, since a lost acknowledgement cannot prove rollback.
/// Records and their provenance are retained after invalidation. Runtime registration is separate.</remarks>
[Experimental("AMEXT001")]
public sealed class Neo4jModuleStore : IModuleStore
{
    private readonly INeo4jTransactionRunner _runner;
    private readonly ModuleResolution _resolution;
    private readonly ModuleMigrator _migrator;
    private readonly ModuleRecordLinks _links;

    /// <summary>Binds one configured store to frozen metadata and optional explicit migration sources.</summary>
    public Neo4jModuleStore(INeo4jTransactionRunner runner, ModuleResolution resolution,
        IReadOnlyList<ModuleMigrationStep>? scripts = null)
    {
        ArgumentNullException.ThrowIfNull(runner); ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.Succeeded) throw new ArgumentException("Successful metadata resolution is required.", nameof(resolution));
        _runner = runner; _resolution = resolution;
        _migrator = new(runner, resolution, scripts ?? [], null); _links = new(resolution);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ProposalOutcome>> WriteAsync(ModuleWriteBatch batch, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(batch.Proposals);
        cancellationToken.ThrowIfCancellationRequested();
        if (batch.Proposals.Count == 0) return [];
        if (batch.Proposals.Any(p => p is null || p.Index < 0 || p.Proposal is null)
            || batch.Proposals.Select(p => p.Index).Distinct().Count() != batch.Proposals.Count)
            throw new ArgumentException("Batch requires distinct nonnegative proposal indices.", nameof(batch));
        ArgumentNullException.ThrowIfNull(batch.Request); ArgumentNullException.ThrowIfNull(batch.Provider);
        var request = batch.Request with
        {
            Messages = batch.Request.Messages?.Select(m => m is null ? null! : m with { }).ToList().AsReadOnly()!,
            ProviderIds = batch.Request.ProviderIds?.ToList().AsReadOnly()!
        };
        var provider = batch.Provider with { };
        var module = _resolution.Modules.SingleOrDefault(m => m.Id == batch.ModuleId);
        if (module is null || batch.ManifestFingerprint != _resolution.ManifestFingerprint || !ModuleIdentity.IsOwnedId(batch.ModuleId, provider.Id))
            return Refused(batch.Proposals, "Batch metadata/provider does not match the bound store.");
        var admitted = new List<AdmittedModuleProposal>(); var outcomes = new List<ProposalOutcome>();
        foreach (var input in batch.Proposals)
        {
            var admission = ModuleProposalAdmission.Admit(_resolution, provider, request, input.Proposal);
            if (admission.Accepted) admitted.Add(input with { Proposal = admission.Proposal! });
            else outcomes.Add(new() { Index = input.Index, Status = ProposalWriteStatus.Refused, Errors = admission.Errors });
        }
        if (admitted.Count == 0) return outcomes.AsReadOnly();
        try
        {
            var written = await _runner.WriteAsync(async transaction =>
            {
                var readiness = await _migrator.PlanInAsync(transaction, cancellationToken).ConfigureAwait(false);
                if (readiness.Problems.Count != 0 || readiness.Pending.Count != 0 || readiness.PendingNamespaces.Count != 0 || readiness.PendingBookkeeping.Count != 0)
                    throw new ModuleRecordRefusal("Module migrations are pending or inconsistent; apply them explicitly before writes.");
                await VerifySourcesAsync(transaction, request, admitted, cancellationToken).ConfigureAwait(false);
                var keys = admitted.Select(p => (p.Proposal, Type: module.Ontology!.Types.Single(t => t.Name == p.Proposal.Type)))
                    .Select(p => (Id: KeyId(module.Id, p.Type.Name, p.Type.Scope == "shared" ? null : request.Owner, p.Proposal.Key), p.Proposal, p.Type))
                    .GroupBy(p => p.Id).Select(g => g.First()).OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
                foreach (var key in keys)
                    await LockAsync(transaction, module.Id, key.Type, key.Proposal.Key, key.Type.Scope == "shared" ? null : request.Owner, cancellationToken).ConfigureAwait(false);
                var results = new List<ProposalOutcome>();
                foreach (var proposal in admitted)
                    results.Add(await WriteOneAsync(transaction, module, request, proposal, batch.AtUtc.ToUniversalTime(), cancellationToken).ConfigureAwait(false));
                return results;
            }, cancellationToken).ConfigureAwait(false);
            outcomes.AddRange(written);
        }
        catch (ModuleRecordRefusal refusal) { outcomes.AddRange(Refused(admitted, refusal.Message)); }
        return outcomes.OrderBy(p => p.Index).ToList().AsReadOnly();
    }

    private static async Task VerifySourcesAsync(IAsyncQueryRunner runner, IngestionRequest request,
        IReadOnlyList<AdmittedModuleProposal> proposals, CancellationToken cancellationToken)
    {
        var ids = proposals.SelectMany(p => p.Proposal.Source.MessageIds).Distinct(StringComparer.Ordinal).ToArray();
        var actual = await RowsAsync(runner, ModuleRecordQueries.Sources,
            new { ids, session = request.SessionId, owner = request.Owner }, cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            var supplied = request.Messages.Single(m => m.Id == id);
            var matches = actual.Where(r => r["id"].As<string>() == id && r["role"].As<string>() == supplied.Role && r["text"].As<string>() == supplied.Text).ToArray();
            if (matches.Length != 1) throw new ModuleRecordRefusal("Source message is absent or does not match the resolved owner/session, role and content.");
        }
    }

    private static async Task<Dictionary<string, object>> LockAsync(IAsyncQueryRunner runner, string module,
        ModuleType type, string key, string? owner, CancellationToken cancellationToken)
    {
        var keyId = KeyId(module, type.Name, owner, key);
        var rows = await RowsAsync(runner, ModuleRecordQueries.LockKey, new { keyId, module, type = type.Name, owner, key }, cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1) throw new ModuleRecordRefusal("Module key identity is ambiguous.");
        var row = rows[0]["row"].As<Dictionary<string, object>>();
        if (Text(row, "module_id") != module || Text(row, "type") != type.Name || Text(row, "owner_id") != owner || Text(row, "key") != key
            || !row.TryGetValue("version", out var version) || version is not long number || number < 0)
            throw new ModuleRecordRefusal("Module key bookkeeping is inconsistent.");
        return row;
    }

    private async Task<ProposalOutcome> WriteOneAsync(IAsyncQueryRunner runner, ModuleManifest module,
        IngestionRequest request, AdmittedModuleProposal input, DateTimeOffset atUtc, CancellationToken cancellationToken)
    {
        var proposal = input.Proposal; var type = module.Ontology!.Types.Single(t => t.Name == proposal.Type);
        var owner = type.Scope == "shared" ? null : request.Owner;
        var keyId = KeyId(module.Id, type.Name, owner, proposal.Key);
        var key = await LockAsync(runner, module.Id, type, proposal.Key, owner, cancellationToken).ConfigureAwait(false);
        var version = key["version"].As<long>();
        var previousId = version == 0 ? null : keyId + ":" + version.ToString(CultureInfo.InvariantCulture);
        if (Text(key, "current_id") is { } current && current != previousId) throw new ModuleRecordRefusal("Module key current reference does not match its last version.");
        var properties = proposal.Properties.ToDictionary(p => p.Key, p => Scalar(p.Value), StringComparer.Ordinal);
        var oldRows = previousId is null ? [] : await RowsAsync(runner, ModuleRecordQueries.Current(type), new { id = previousId, properties, now = atUtc.ToString("O") }, cancellationToken).ConfigureAwait(false);
        if (previousId is not null && oldRows.Count != 1) throw new ModuleRecordRefusal("Module version history is incomplete.");
        var old = oldRows.SingleOrDefault();
        if (old is not null)
        {
            var record = old["row"].As<Dictionary<string, object>>();
            if (Text(record, "key_id") != keyId || Text(record, "key") != proposal.Key || Text(record, "owner_id") != owner
                || Text(record, "superseded_by") is not null)
                throw new ModuleRecordRefusal("Current record identity, owner or supersession does not match its key.");
        }
        var live = old is not null && old["live"].As<bool>();
        if ((Text(key, "current_id") is not null) != live) throw new ModuleRecordRefusal("Module key liveness is inconsistent.");
        if (proposal.Kind == ProposalKind.Invalidate)
        {
            if (!live) return Outcome(input.Index, ProposalWriteStatus.NoOp, previousId);
            if (old!["newer"].As<bool>()) throw new ModuleRecordRefusal("Write clock precedes the current version.");
            await OneAsync(runner, ModuleRecordQueries.Invalidate(type.Name), new { id = previousId, keyId, now = atUtc.ToString("O") }, cancellationToken).ConfigureAwait(false);
            return Outcome(input.Index, ProposalWriteStatus.Invalidated, previousId);
        }
        var links = await _links.ResolveAsync(runner, module, proposal, previousId, owner, cancellationToken).ConfigureAwait(false);
        if (live && old!["equal"].As<bool>() && !links.Changed) return Outcome(input.Index, ProposalWriteStatus.NoOp, previousId);
        if (old is not null && old["newer"].As<bool>()) throw new ModuleRecordRefusal("Write clock precedes the last version.");
        var nextVersion = checked(version + 1); var id = keyId + ":" + nextVersion.ToString(CultureInfo.InvariantCulture);
        await OneAsync(runner, ModuleRecordQueries.Create(type), new { id, keyId, key = proposal.Key, owner,
            now = atUtc.ToString("O"), session = request.SessionId, sources = proposal.Source.MessageIds.ToArray(), excerpt = proposal.Source.Excerpt, properties }, cancellationToken).ConfigureAwait(false);
        foreach (var link in links.Links)
            await OneAsync(runner, ModuleRecordQueries.Link(type.Name, link.Relation, link.TargetLabel), new { id, target = link.TargetId, owner }, cancellationToken).ConfigureAwait(false);
        if (previousId is not null)
            await OneAsync(runner, ModuleRecordQueries.Supersede(type.Name), new { previous = previousId, id, now = atUtc.ToString("O") }, cancellationToken).ConfigureAwait(false);
        await OneAsync(runner, ModuleRecordQueries.SetCurrent, new { keyId, version = nextVersion, id }, cancellationToken).ConfigureAwait(false);
        return Outcome(input.Index, ProposalWriteStatus.Stored, id);
    }

    private static object Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString()!, JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number when value.TryGetInt64(out var integer) => integer,
        JsonValueKind.Number => value.GetDouble(), _ => throw new ModuleRecordRefusal("Unsupported stored scalar.")
    };
    private static string KeyId(string module, string type, string? owner, string key) => "module:" +
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[] { module, type, owner, key }))).ToLowerInvariant();
    private static ProposalOutcome Outcome(int index, ProposalWriteStatus status, string? id) => new() { Index = index, Status = status, RecordId = id };
    private static IReadOnlyList<ProposalOutcome> Refused(IReadOnlyList<AdmittedModuleProposal> proposals, string error) =>
        proposals.Select(p => new ProposalOutcome { Index = p.Index, Status = ProposalWriteStatus.Refused, Errors = [error] }).ToList().AsReadOnly();
}
