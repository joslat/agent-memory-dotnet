// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Microsoft.Extensions.Logging;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

/// <summary>Optional owner/brain-scoped knowledge amendments; all writes serialize with the committed protection policy.</summary>
[Experimental("AMEXT001")]
public sealed class Neo4jPackLocalStore(INeo4jTransactionRunner runner, IMemoryIsolationPolicy isolation,
    IReadOnlyList<KnowledgePack> packs, TimeProvider? timeProvider = null) : IPackLocalStore
{
    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly ILogger? _logger;

    /// <summary>
    /// The same store; each mount and detach writes one log line, and a failure says its pack, step and
    /// <see cref="PackFailure.Code"/>.
    /// </summary>
    public Neo4jPackLocalStore(INeo4jTransactionRunner runner, IMemoryIsolationPolicy isolation, IReadOnlyList<KnowledgePack> packs,
        TimeProvider? timeProvider, ILogger<Neo4jPackLocalStore>? logger)
        : this(runner, isolation, packs, timeProvider) => _logger = logger;

    private PackOperationTrace Trace(string operation, PackLocalIdentity identity) =>
        PackOperationTrace.Start(operation, identity.PackId ?? "", packs.FirstOrDefault(p => p.Id == identity.PackId && p.Digest == identity.BaseDigest)?.Version,
            string.IsNullOrEmpty(identity.BaseDigest) ? null : identity.BaseDigest, _logger);

    private static string Summary(PackLocalPolicy policy) =>
        $"revision {policy.Revision}, {(policy.Active ? "active" : "inactive")}, {(policy.WriteProtected ? "write-protected" : "writable")}";

    private Dictionary<string, object?> Parameters(PackLocalIdentity identity, bool writing = false, bool requirePack = true)
    {
        foreach (var value in new[] { identity.ApplicationId, identity.OwnerId, identity.PackId })
            ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!ModuleIdentity.IsValidModuleId(identity.PackId)) throw new ArgumentException("A valid knowledge pack ID is required.", nameof(identity));
        var scope = isolation.ResolveReadScope(null, identity.OwnerId, "pack.local", MemoryOperationAccess.Tenant);
        if (scope.OwnerId != identity.OwnerId) throw new InvalidOperationException("The local-copy owner does not match the resolved scope.");
        if (writing && isolation.ResolveWriteOwner(identity.OwnerId, "pack.local", MemoryOperationAccess.Tenant) != identity.OwnerId)
            throw new InvalidOperationException("The local-copy owner does not match the resolved write scope.");
        KnowledgePack? pack = null;
        if (requirePack)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(identity.BaseDigest);
            pack = packs.SingleOrDefault(p => p.Id == identity.PackId && p.Digest == identity.BaseDigest)
                ?? throw new InvalidOperationException("The pack digest is not bound to this host.");
            if (pack.ValidUntil is { } until && _clock.GetUtcNow() > until) throw new InvalidOperationException("The pack has expired.");
        }
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { identity.ApplicationId, identity.OwnerId, identity.PackId }))));
        return new() { ["id"] = id, ["application"] = identity.ApplicationId, ["owner"] = identity.OwnerId,
            ["pack"] = identity.PackId, ["digest"] = identity.BaseDigest, ["version"] = pack?.Version ?? "",
            ["historyId"] = Guid.NewGuid().ToString("N"), ["now"] = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture) };
    }

    /// <inheritdoc/>
    public async Task<PackLocalPolicy> MountAsync(PackLocalIdentity identity, bool writeProtected = true, CancellationToken cancellationToken = default)
    {
        using var trace = Trace(PackOperations.Mount, identity);
        try
        {
            trace.Step = PackSteps.Validate;
            var args = Parameters(identity, writing: true); args["protected"] = writeProtected;
            trace.Step = PackSteps.LocalCopy;
            var policy = await MountInAsync(identity, args, cancellationToken).ConfigureAwait(false);
            trace.Completed(Summary(policy));
            return policy;
        }
        catch (Exception error)
        {
            trace.Failed(error, cancellationToken);
            throw;
        }
    }

    private Task<PackLocalPolicy> MountInAsync(PackLocalIdentity identity, Dictionary<string, object?> args, CancellationToken cancellationToken) =>
        runner.WriteAsync(async transaction =>
        {
            if ((await RowsAsync(transaction, PackLocalQueries.Ready, new { }, cancellationToken).ConfigureAwait(false)).Single()["count"].As<long>() != 4)
                throw new InvalidOperationException("Activate and migrate the pack-local-state schema extension before mounting editable knowledge.");
            await RowsAsync(transaction, PackLocalQueries.Gate, args, cancellationToken).ConfigureAwait(false);
            var policy = Policy((await RowsAsync(transaction, PackLocalQueries.Mount, args, cancellationToken).ConfigureAwait(false)).Single());
            if (policy.BaseDigest == identity.BaseDigest && policy.Active)
            {
                await RowsAsync(transaction, PackLocalQueries.RecordRevision, args, cancellationToken).ConfigureAwait(false);
                return policy;
            }
            var snapshot = await ReadInAsync(transaction, args, cancellationToken).ConfigureAwait(false);
            ValidateUpgrade(identity, snapshot);
            Validate(identity, snapshot.Changes.Where(c => c.Proposal is not null).Select(c => c.Proposal!).ToList());
            // A detached copy is reactivated by its own rule (R-HOST-069); an active one is rebased, its policy kept.
            var rebased = Policy((await RowsAsync(transaction, policy.Active ? PackLocalQueries.Rebase : PackLocalQueries.Reactivate, args, cancellationToken)
                .ConfigureAwait(false)).Single());
            await RowsAsync(transaction, PackLocalQueries.RecordRevision, args, cancellationToken).ConfigureAwait(false);
            return rebased;
        }, cancellationToken);

    /// <summary>Reads the committed pin without rebasing it. No mount is created.</summary>
    public Task<IReadOnlyDictionary<string, PackLocalStatus>> PeekManyAsync(IReadOnlyList<PackLocalIdentity> identities, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identities); cancellationToken.ThrowIfCancellationRequested();
        if (identities.Count > 32 || identities.Any(i => i is null)) throw new ArgumentException("A status batch contains at most 32 non-null identities.", nameof(identities));
        if (identities.Count == 0) return Task.FromResult<IReadOnlyDictionary<string, PackLocalStatus>>(new Dictionary<string, PackLocalStatus>().AsReadOnly());
        var first = identities[0];
        if (identities.Any(i => i.ApplicationId != first.ApplicationId || i.OwnerId != first.OwnerId)) throw new ArgumentException("A status batch belongs to one owner and brain.", nameof(identities));
        var args = identities.DistinctBy(i => i.PackId).Select(i => Parameters(i, requirePack: false)).ToArray();
        return runner.ReadAsync<IReadOnlyDictionary<string, PackLocalStatus>>(async tx =>
        {
            var rows = await RowsAsync(tx, PackLocalQueries.ReadStatuses, new { ids = args.Select(a => a["id"]).ToArray(), packs = args.Select(a => a["pack"]).ToArray(), owner = first.OwnerId, application = first.ApplicationId }, cancellationToken).ConfigureAwait(false);
            if (rows.Count > args.Length) throw new InvalidDataException("The scoped status batch is inconsistent.");
            return rows.ToDictionary(r => r["pack"].As<string>(), r => new PackLocalStatus { Policy = Policy(r), HasAssertions = r["amended"].As<bool>() }, StringComparer.Ordinal).AsReadOnly();
        }, cancellationToken);
    }

    /// <summary>Reads the committed pin without rebasing it. No mount is created.</summary>
    public Task<PackLocalPolicy?> PeekAsync(PackLocalIdentity identity, CancellationToken cancellationToken = default)
    {
        var args = Parameters(identity, requirePack: false);
        return runner.ReadAsync(async tx =>
        {
            var rows = await RowsAsync(tx, PackLocalQueries.ReadPolicy, args, cancellationToken).ConfigureAwait(false);
            return rows.Count == 0 ? null : Policy(rows.Single());
        }, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<PackLocalPolicy> DetachAsync(PackLocalIdentity identity, long expectedRevision, CancellationToken cancellationToken = default)
    {
        using var trace = Trace(PackOperations.Detach, identity);
        try
        {
            trace.Step = PackSteps.Validate;
            var args = Parameters(identity, writing: true, requirePack: false);
            args["protected"] = true; args["automatic"] = false; args["active"] = false;
            trace.Step = PackSteps.LocalCopy;
            var detached = await runner.WriteAsync(async tx =>
            {
                var policy = await LockedAsync(tx, args, expectedRevision, cancellationToken).ConfigureAwait(false);
                if (!policy.Active && policy.WriteProtected && !policy.AutomaticLearning) return policy;
                var changed = Policy((await RowsAsync(tx, PackLocalQueries.Policy, args, cancellationToken).ConfigureAwait(false)).Single());
                await RowsAsync(tx, PackLocalQueries.RecordRevision, args, cancellationToken).ConfigureAwait(false);
                return changed;
            }, cancellationToken).ConfigureAwait(false);
            trace.Completed(Summary(detached));
            return detached;
        }
        catch (Exception error)
        {
            trace.Failed(error, cancellationToken);
            throw;
        }
    }

    /// <summary>Checks a proposed mount without creating or changing its committed policy. Mount rechecks under its write lock.</summary>
    public Task CheckMountAsync(PackLocalIdentity identity, CancellationToken cancellationToken = default)
    {
        var args = Parameters(identity, writing: true);
        return runner.ReadAsync(async tx =>
        {
            if ((await RowsAsync(tx, PackLocalQueries.ReadPolicy, args, cancellationToken).ConfigureAwait(false)).Count == 0) return;
            var snapshot = await ReadInAsync(tx, args, cancellationToken).ConfigureAwait(false);
            ValidateUpgrade(identity, snapshot);
            Validate(identity, snapshot.Changes.Where(c => c.Proposal is not null).Select(c => c.Proposal!).ToList());
        }, cancellationToken);
    }

    private void ValidateUpgrade(PackLocalIdentity identity, PackLocalSnapshot snapshot)
    {
        if (snapshot.Policy.BaseDigest == identity.BaseDigest) return;
        var target = packs.Single(p => p.Id == identity.PackId && p.Digest == identity.BaseDigest);
        if (ModuleVersion.TryParse(snapshot.Policy.BaseVersion, out var previousVersion) && previousVersion.CompareTo(ModuleVersion.Parse(target.Version)) >= 0)
            throw new InvalidOperationException("A reconnect cannot downgrade or replace a same-version published base.");
        if (!snapshot.Changes.Any(c => c.Proposal is not null)) return;
        var previous = packs.SingleOrDefault(p => p.Id == identity.PackId && p.Digest == snapshot.Policy.BaseDigest)
            ?? throw new InvalidOperationException("Retain the verified prior base before upgrading a personal copy.");
        foreach (var change in snapshot.Changes.Where(c => c.Proposal is not null))
            if (previous.Proposals.Any(p => p.Type == change.Type && p.Key == change.Key)
                && !target.Proposals.Any(p => p.Type == change.Type && p.Key == change.Key))
                throw new InvalidOperationException("The new base removed an overridden published key. Resolve the conflict against the retained base first.");
    }

    /// <inheritdoc/>
    public async Task<PackLocalSnapshot> ReadAsync(PackLocalIdentity identity, DateTimeOffset? systemAsOfUtc = null, CancellationToken cancellationToken = default)
    {
        var args = Parameters(identity); args["asOf"] = systemAsOfUtc?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        return await runner.ReadAsync(async transaction =>
        {
            var snapshot = await ReadInAsync(transaction, args, cancellationToken).ConfigureAwait(false);
            if (systemAsOfUtc is null && snapshot.Policy.BaseDigest != identity.BaseDigest)
                throw new InvalidOperationException("The mounted base changed; reconnect before reading local knowledge.");
            return snapshot;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<PackLocalPolicy> ConfigureAsync(PackLocalIdentity identity, long expectedRevision, bool writeProtected,
        bool automaticLearning, bool active = true, CancellationToken cancellationToken = default)
    {
        var args = Parameters(identity, writing: true); args["protected"] = writeProtected; args["automatic"] = automaticLearning; args["active"] = active;
        return await runner.WriteAsync(async transaction =>
        {
            var policy = await LockedAsync(transaction, args, expectedRevision, cancellationToken).ConfigureAwait(false);
            if (policy.BaseDigest != identity.BaseDigest) throw new InvalidOperationException("The mounted base changed; refresh before changing protection.");
            var changed = Policy((await RowsAsync(transaction, PackLocalQueries.Policy, args, cancellationToken).ConfigureAwait(false)).Single());
            await RowsAsync(transaction, PackLocalQueries.RecordRevision, args, cancellationToken).ConfigureAwait(false);
            return changed;
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task WriteAsync(PackLocalIdentity identity, long expectedRevision, ModuleProposal proposal, bool automatic = false,
        CancellationToken cancellationToken = default) => ChangeAsync(identity, expectedRevision, proposal.Type, proposal.Key, proposal, automatic, cancellationToken);

    /// <inheritdoc/>
    public Task RestoreAsync(PackLocalIdentity identity, long expectedRevision, string type, string key, CancellationToken cancellationToken = default) =>
        ChangeAsync(identity, expectedRevision, type, key, null, false, cancellationToken);

    private async Task ChangeAsync(PackLocalIdentity identity, long expectedRevision, string type, string key, ModuleProposal? proposal,
        bool automatic, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type); ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var args = Parameters(identity, writing: true); args["type"] = type; args["key"] = key;
        args["changeId"] = Guid.NewGuid().ToString("N"); args["actor"] = automatic ? "system" : "user";
        args["payload"] = proposal is null ? null : JsonSerializer.Serialize(proposal);
        if (args["payload"] is string payload && Encoding.UTF8.GetByteCount(payload) > KnowledgePack.MaximumLineBytes)
            throw new InvalidOperationException("The local record exceeds 64 KiB.");
        proposal = args["payload"] is string serialized ? Parse(serialized) : null;
        await runner.WriteAsync(async transaction =>
        {
            await RowsAsync(transaction, PackLocalQueries.Gate, args, cancellationToken).ConfigureAwait(false);
            var policy = await LockedAsync(transaction, args, expectedRevision, cancellationToken).ConfigureAwait(false);
            if (!policy.Active || policy.BaseDigest != identity.BaseDigest) throw new InvalidOperationException("The mount is inactive or its base changed.");
            if (policy.WriteProtected || (automatic && !policy.AutomaticLearning)) throw new InvalidOperationException("This pack is write protected or automatic learning is disabled.");
            var current = await ReadInAsync(transaction, args, cancellationToken).ConfigureAwait(false);
            if (current.Erasures.Any(e => e.Erases(type, key))) throw new InvalidOperationException("The publisher withdrew this record; no local writer or restore may revive it.");
            var pack = packs.Single(p => p.Id == identity.PackId && p.Digest == identity.BaseDigest);
            if (proposal is null && !pack.Proposals.Any(p => p.Type == type && p.Key == key)
                && !current.Changes.Any(c => c.Type == type && c.Key == key && c.Proposal is not null))
                throw new InvalidOperationException("There is no local or published record to restore.");
            var effective = current.Changes.Where(c => c.Proposal is not null && (c.Type != type || c.Key != key)).Select(c => c.Proposal!).ToList();
            if (proposal is not null) effective.Add(proposal);
            Validate(identity, effective);
            if (proposal is not null)
                foreach (var link in proposal.Links)
                {
                    var relation = pack.Manifest.Ontology!.Relations.Single(r => r.Name == link.Relation && r.From == type);
                    if (current.Erasures.Any(e => e.Erases(relation.To, link.Target))) throw new InvalidOperationException("A local link cannot name a withdrawn record.");
                }
            if (automatic || proposal?.Source.MessageIds.Count > 0)
            {
                if (proposal?.Source.MessageIds is not { Count: > 0 }) throw new InvalidOperationException("Automatic changes require persisted source messages.");
                var sourceRows = await RowsAsync(transaction, PackLocalQueries.Sources, new { ids = proposal.Source.MessageIds, owner = identity.OwnerId }, cancellationToken).ConfigureAwait(false);
                if (sourceRows.Count != proposal.Source.MessageIds.Distinct(StringComparer.Ordinal).Count())
                    throw new InvalidOperationException("Automatic source messages do not belong to this owner.");
                if (string.IsNullOrWhiteSpace(proposal.Source.Excerpt) || !sourceRows.Any(r => r["content"].As<string>().Contains(proposal.Source.Excerpt, StringComparison.Ordinal)))
                    throw new InvalidOperationException("The source excerpt must occur in an actual persisted user message.");
            }
            args["now"] = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            await RowsAsync(transaction, PackLocalQueries.Change, args, cancellationToken).ConfigureAwait(false);
            await RowsAsync(transaction, PackLocalQueries.RecordRevision, args, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    private void Validate(PackLocalIdentity identity, IReadOnlyList<ModuleProposal> changes)
    {
        var pack = packs.Single(p => p.Id == identity.PackId && p.Digest == identity.BaseDigest);
        var keys = pack.Proposals.Select(p => (p.Type, p.Key)).Concat(changes.Select(p => (p.Type, p.Key))).ToHashSet();
        if (keys.Count > KnowledgePack.MaximumRecords) throw new InvalidOperationException("The local copy exceeds its record bound.");
        foreach (var proposal in changes)
        {
            var errors = pack.ValidateLocalAssertion(proposal);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join(" ", errors));
            foreach (var link in proposal.Links)
            {
                var relation = pack.Manifest.Ontology!.Relations.Single(r => r.Name == link.Relation && r.From == proposal.Type);
                if (!keys.Contains((relation.To, link.Target))) throw new InvalidOperationException("A local link has no target in the working copy.");
            }
        }
    }

    private static async Task<PackLocalPolicy> LockedAsync(IAsyncQueryRunner transaction, Dictionary<string, object?> args, long revision, CancellationToken ct)
    {
        var rows = await RowsAsync(transaction, PackLocalQueries.Lock, args, ct).ConfigureAwait(false);
        if (rows.Count != 1) throw new InvalidOperationException("This brain has no active local mount.");
        var policy = Policy(rows[0]);
        if (policy.Revision != revision) throw new InvalidOperationException("The pack changed while this operation was queued. Refresh and try again.");
        return policy;
    }

    private static async Task<PackLocalSnapshot> ReadInAsync(IAsyncQueryRunner transaction, Dictionary<string, object?> args, CancellationToken ct)
    {
        if (!args.ContainsKey("asOf")) args["asOf"] = null;
        var rows = await RowsAsync(transaction, args["asOf"] is null ? PackLocalQueries.Read : PackLocalQueries.ReadAsOf, args, ct).ConfigureAwait(false);
        if (rows.Count != 1) throw new InvalidOperationException("The local mount or requested historical base pin is not present.");
        var changes = rows[0]["changes"].As<List<Dictionary<string, object>>>();
        if (changes.Count > KnowledgePack.MaximumRecords) throw new InvalidOperationException("The local state exceeds its record bound.");
        var erasures = await ErasuresAsync(transaction, args, ct).ConfigureAwait(false);
        return new() { Policy = Policy(rows[0]), Changes = changes.Select(Change).Where(c => !erasures.Any(e => e.Erases(c.Type, c.Key))).ToList().AsReadOnly(), Erasures = erasures };
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PackLocalChange>> HistoryAsync(PackLocalIdentity identity, string type, string key, CancellationToken cancellationToken = default)
    {
        var args = Parameters(identity); args["type"] = type; args["key"] = key;
        return await runner.ReadAsync(async transaction =>
        {
            var erasures = await ErasuresAsync(transaction, args, cancellationToken).ConfigureAwait(false);
            if (erasures.Any(e => e.Erases(type, key))) return (IReadOnlyList<PackLocalChange>)[];
            var rows = await RowsAsync(transaction, PackLocalQueries.History, args, cancellationToken).ConfigureAwait(false);
            return rows.Select(r => Change(r["row"].As<Dictionary<string, object>>())).ToList().AsReadOnly();
        }, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<ModuleErasure>> ErasuresAsync(IAsyncQueryRunner transaction, Dictionary<string, object?> args, CancellationToken ct)
    {
        var rows = await RowsAsync(transaction, PackLocalQueries.Tombstones, args, ct).ConfigureAwait(false);
        if (rows.Count > 100000) throw new InvalidOperationException("The pack erasure set exceeds the read bound.");
        return rows.GroupBy(r => (Statement: r["statement"].As<string>(), Salt: r["salt"].As<string>(), Reason: r["reason"].As<string>()))
            .Select(g => new ModuleErasure { PackId = (string)args["pack"]!, Statement = g.Key.Statement, Salt = g.Key.Salt, Reason = g.Key.Reason,
                IssuedAt = g.First()["at"].As<ZonedDateTime>().ToDateTimeOffset(), Records = g.Select(r => new ModuleErasedRecord(r["type"].As<string>(), null, r["hash"].As<string>())).ToList().AsReadOnly() })
            .ToList().AsReadOnly();
    }

    private static PackLocalPolicy Policy(IRecord row)
    {
        var p = row["policy"].As<Dictionary<string, object>>();
        return new() { Revision = p["revision"].As<long>(), WriteProtected = p["write_protected"].As<bool>(),
            AutomaticLearning = p["automatic_learning"].As<bool>(), Active = p["active"].As<bool>(), BaseDigest = p["base_digest"].As<string>(),
            BaseVersion = p.TryGetValue("base_version", out var version) ? version.As<string>() : "" };
    }
    private static PackLocalChange Change(Dictionary<string, object> row) => new()
    {
        Id = row["id"].As<string>(), Type = row["type"].As<string>(), Key = row["key"].As<string>(), Actor = row["actor"].As<string>(),
        CreatedAtUtc = row["created_at"].As<ZonedDateTime>().ToDateTimeOffset(),
        InvalidatedAtUtc = row.TryGetValue("invalidated_at", out var at) ? at.As<ZonedDateTime>().ToDateTimeOffset() : null,
        Proposal = row.TryGetValue("payload", out var data) ? Parse(data.As<string>()) : null,
    };

    internal static ModuleProposal Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > KnowledgePack.MaximumLineBytes) throw new InvalidDataException("The local payload exceeds its bound.");
        var proposal = JsonSerializer.Deserialize<ModuleProposal>(json, new JsonSerializerOptions { MaxDepth = 32 })
            ?? throw new InvalidDataException("The local assertion is empty.");
        return proposal with { Properties = proposal.Properties.ToDictionary(p => p.Key, p => p.Value.Clone(), StringComparer.Ordinal).AsReadOnly(),
            Links = proposal.Links.Select(l => l with { }).ToList().AsReadOnly(),
            Source = proposal.Source with { MessageIds = proposal.Source.MessageIds.ToList().AsReadOnly() } };
    }
}
