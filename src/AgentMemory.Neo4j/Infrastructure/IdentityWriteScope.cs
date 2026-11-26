using AgentMemory.Neo4j.Queries;
using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>Callback-owned mutexes shared by repositories using the same transaction runner.</summary>
internal static class IdentityWriteScope
{
    private static readonly AsyncLocal<Lease?> Current = new();

    /// <param name="runner">The transaction runner the lease belongs to.</param>
    /// <param name="scopes">The scope locks held now.</param>
    /// <param name="deferred">
    /// Scopes the lease may take later in the same transaction, when a step first reads or writes their rows
    /// (<see cref="HoldAsync"/>): RVW-IDENTITY-SHARED-LOCK, an owner ingestion takes the shared scope only when one of its
    /// decisions involves a shared entity, so owners whose ingestions touch none no longer wait on each other.
    /// </param>
    public static IDisposable Enter(INeo4jTransactionRunner runner, IReadOnlyList<string> scopes, IReadOnlyList<string>? deferred = null)
    {
        if (Current.Value is not null) throw new InvalidOperationException("An identity write scope is already active.");
        var lease = new Lease(runner, scopes, deferred ?? []); Current.Value = lease; return lease;
    }

    public static bool Reuse(INeo4jTransactionRunner runner, IEnumerable<string> scopes)
    {
        if (Current.Value is not { } lease) return false;
        if (!lease.IsActive || !ReferenceEquals(lease.Runner, runner) || scopes.Any(scope => !lease.Scopes.Contains(scope) && !lease.Deferred.Contains(scope)))
            throw new InvalidOperationException("Identity storage cannot outlive, widen or leave its owned write scope.");
        return true;
    }

    /// <summary>Whether the active lease deferred this scope (and has not taken it yet).</summary>
    public static bool Defers(string scope) => Current.Value is { IsActive: true } lease && lease.Deferred.Contains(scope);

    /// <summary>
    /// Takes a scope the active lease deferred, in the lease's transaction, and keeps it to commit. True when it was taken
    /// now; false when there is no lease, it was held already or the lease never deferred it (held from the start).
    /// Deadlock-free against the scope order: the only scope deferred is the shared one, and a holder of the shared scope
    /// alone (an ownerless ingestion) never asks for an owned scope or an owner's rows.
    /// </summary>
    public static async Task<bool> HoldAsync(IAsyncQueryRunner query, string scope, CancellationToken cancellationToken)
    {
        if (Current.Value is not { IsActive: true } lease || !lease.Deferred.Contains(scope)) return false;
        var cursor = await query.RunAsync(IdentityDecisionQueries.AcquireScope, new { ownerScope = scope }).ConfigureAwait(false);
        var rows = await cursor.ToListAsync(cancellationToken).ConfigureAwait(false);
        if (rows.Count != 1 || rows[0]["acquired"].As<long>() != 1)
            throw new InvalidOperationException("Identity scope-lock infrastructure is missing or duplicated; migrate the owned schema.");
        lease.Deferred.Remove(scope);
        lease.Scopes.Add(scope);
        return true;
    }

    private sealed class Lease(INeo4jTransactionRunner runner, IReadOnlyList<string> scopes, IReadOnlyList<string> deferred) : IDisposable
    {
        private int _active = 1;
        public INeo4jTransactionRunner Runner { get; } = runner;
        public HashSet<string> Scopes { get; } = scopes.ToHashSet(StringComparer.Ordinal);
        public HashSet<string> Deferred { get; } = deferred.ToHashSet(StringComparer.Ordinal);
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public void Dispose()
        {
            Interlocked.Exchange(ref _active, 0);
            Current.Value = null;
        }
    }
}
