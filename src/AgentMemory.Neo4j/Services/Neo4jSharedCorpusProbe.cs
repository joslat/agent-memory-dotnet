using System.Collections.Concurrent;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using AgentMemory.Neo4j.Infrastructure;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Services;

/// <summary>
/// 37.1b. Answers <see cref="ISharedCorpusProbe"/> with one existence query per kind and store, cached briefly: "none"
/// for <see cref="NoneFor"/>, so a store that gains shared knowledge is searched within that time; "some" for
/// <see cref="SomeFor"/>, since a store rarely loses all of it and a stale "some" only costs one empty search.
/// </summary>
/// <remarks>
/// Facts and entities are found by an index seek on <c>owner_key = '*'</c>; preferences carry no key, and the
/// existence check stops at the first shared one (a label scan only when there is none, at most once per window).
/// The answer is per store: the ambient application scope selects the database the query runs in and the cache key.
/// A shared write in this process (<see cref="Saw"/>, called by the fact, entity and preference repositories) turns
/// the answer to "some" at once; only a write from another process waits out <see cref="NoneFor"/>.
/// </remarks>
internal sealed class Neo4jSharedCorpusProbe : ISharedCorpusProbe
{
    internal static readonly TimeSpan NoneFor = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan SomeFor = TimeSpan.FromMinutes(10);

    private const string EntityQuery = "RETURN EXISTS { MATCH (e:Entity {owner_key: '*'}) } AS shared";
    private const string FactQuery = "RETURN EXISTS { MATCH (f:Fact {owner_key: '*'}) } AS shared";
    private const string PreferenceQuery = "RETURN EXISTS { MATCH (p:Preference) WHERE p.owner_id IS NULL } AS shared";

    private readonly INeo4jTransactionRunner _tx;
    private readonly IMemoryStoreContext? _store;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<(string? Store, SharedKind Kind), (bool Shared, DateTimeOffset Until)> _answers = new();

    public Neo4jSharedCorpusProbe(INeo4jTransactionRunner tx, IMemoryStoreContext? store = null, TimeProvider? time = null)
    {
        _tx = tx;
        _store = store;
        _time = time ?? TimeProvider.System;
    }

    public async ValueTask<bool> HasSharedAsync(SharedKind kind, CancellationToken cancellationToken)
    {
        var key = (_store?.ApplicationId, kind);
        var now = _time.GetUtcNow();
        if (_answers.TryGetValue(key, out var known) && known.Until > now) return known.Shared;

        var cypher = kind switch
        {
            SharedKind.Entity => EntityQuery,
            SharedKind.Fact => FactQuery,
            _ => PreferenceQuery,
        };
        var shared = await _tx.ReadAsync(async runner =>
        {
            var cursor = await runner.RunAsync(cypher).ConfigureAwait(false);
            var record = await cursor.SingleAsync().ConfigureAwait(false);
            return record["shared"].As<bool>();
        }, cancellationToken).ConfigureAwait(false);
        _answers[key] = (shared, now + (shared ? SomeFor : NoneFor));
        return shared;
    }

    public void Saw(SharedKind kind) => _answers[(_store?.ApplicationId, kind)] = (true, _time.GetUtcNow() + SomeFor);
}
