// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;
using static AgentMemory.Extensibility.Neo4j.Storage.ModuleRecordTransaction;

namespace AgentMemory.Extensibility.Neo4j.Storage;

// Trusted canonical erasure only. Ordinary supersession and restore remain non-destructive.
[Experimental("AMEXT001")]
internal sealed class PackLocalErasureRunner(INeo4jTransactionRunner inner, ModuleManifest pack,
    IReadOnlyList<ModuleErasure> erasures) : INeo4jTransactionRunner
{
    public Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
    public Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) => inner.ReadAsync(work, cancellationToken);
    public bool? InWriteUnit => inner.InWriteUnit;
    public Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default) =>
        WriteAsync(async tx => { await work(tx).ConfigureAwait(false); return true; }, cancellationToken);
    public Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) => inner.WriteAsync(async tx =>
    {
        await RowsAsync(tx, PackLocalQueries.Gate, new { pack = pack.Id }, cancellationToken).ConfigureAwait(false);
        var result = await work(tx).ConfigureAwait(false);
        var rows = await RowsAsync(tx, PackLocalQueries.ErasureCandidates, new { pack = pack.Id }, cancellationToken).ConfigureAwait(false);
        if (rows.Count > 100000) throw new InvalidOperationException("The local erasure maintenance bound was exceeded; no erasure was committed.");
        var gone = rows.Where(row =>
        {
            var type = row["type"].As<string>(); var key = row["key"].As<string>();
            if (Withdrawn(type, key)) return true;
            if (row["payload"] is not string payload) return false;
            var proposal = Neo4jPackLocalStore.Parse(payload);
            return proposal.Links.Any(link => pack.Ontology!.Relations.Any(r => r.From == type && r.Name == link.Relation && Withdrawn(r.To, link.Target)));
        }).Select(row => row["id"].As<string>()).ToArray();
        if (gone.Length > 0) await RowsAsync(tx, PackLocalQueries.PurgeWithdrawn, new { ids = gone }, cancellationToken).ConfigureAwait(false);
        return result;
    }, cancellationToken);

    bool Withdrawn(string type, string key) => erasures.Any(e => e.PackId == pack.Id && e.Erases(type, key));
}
